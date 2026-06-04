/**
 * PixiJS v8 renderer for the first-blood "BLOOD NOVA" slam — a faithful GPU port
 * of the nova sample: a white-hot core flash that cools to a crimson bloom,
 * radial light rays, ~130 colour-temperature sparks (white-hot -> red -> dark)
 * with trails, lingering twinkling embers, a dark-red smoke body, and a two-pass
 * shockwave — all additive over a focus vignette.
 *
 * Per-particle colour is Pixi sprite .tint on one white soft-glow texture (the
 * sample's per-particle radial gradients, but free on the GPU). Replaces the old
 * DOM/CSS splash (the first-blood lag). Full-viewport canvas at z-index 94 (under
 * the DOM FB text, z-95); display:none except while playing. Prefers WebGPU.
 */
import { Application, Container, Graphics, Sprite, Texture } from 'pixi.js'

// BLOOD NOVA palette (RGB), matching the chosen sample.
const HOT = [255, 236, 236], MID = [255, 80, 100], COOL = [210, 16, 44], DARK = [90, 6, 18], RINGC = [255, 90, 110]
const N_SPARK = 130, N_EMBER = 22, N_RAY = 26, N_SMOKE = 8
const GS = 128 // glow texture size

const hex = (c: number[]) => ((c[0] & 255) << 16) | ((c[1] & 255) << 8) | (c[2] & 255)
const mix = (a: number[], b: number[], t: number) => [a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t]
// white-hot -> mid -> cool -> dark, by heat 1..0 (sample temp())
function tempHex(h: number): number {
  h = h < 0 ? 0 : h > 1 ? 1 : h
  const c = h > 0.75 ? mix(MID, HOT, (h - 0.75) / 0.25) : h > 0.4 ? mix(COOL, MID, (h - 0.4) / 0.35) : mix(DARK, COOL, h / 0.4)
  return hex(c)
}
const lerp = (a: number, b: number, t: number) => a + (b - a) * t
const clamp01 = (v: number) => (v < 0 ? 0 : v > 1 ? 1 : v)

// white soft-glow texture (alpha 1 -> .5 @ .4 -> 0), tinted per use.
function glowTex(): Texture {
  const c = document.createElement('canvas'); c.width = c.height = GS
  const g = c.getContext('2d')!, r = GS / 2, grd = g.createRadialGradient(r, r, 0, r, r, r)
  grd.addColorStop(0, 'rgba(255,255,255,1)'); grd.addColorStop(0.4, 'rgba(255,255,255,0.5)'); grd.addColorStop(1, 'rgba(255,255,255,0)')
  g.fillStyle = grd; g.beginPath(); g.arc(r, r, r, 0, Math.PI * 2); g.fill()
  return Texture.from(c)
}
// ray streak: hot at the base -> mid -> cool -> transparent tip, gaussian across width.
function streakTex(): Texture {
  const W = 256, H = 48, c = document.createElement('canvas'); c.width = W; c.height = H; const g = c.getContext('2d')!
  const lg = g.createLinearGradient(0, 0, W, 0)
  lg.addColorStop(0, `rgba(${HOT[0]},${HOT[1]},${HOT[2]},1)`); lg.addColorStop(0.45, `rgba(${MID[0]},${MID[1]},${MID[2]},0.85)`)
  lg.addColorStop(0.8, `rgba(${COOL[0]},${COOL[1]},${COOL[2]},0.3)`); lg.addColorStop(1, `rgba(${COOL[0]},${COOL[1]},${COOL[2]},0)`)
  for (let y = 0; y < H; y++) { const d = Math.abs(y - H / 2) / (H / 2); g.globalAlpha = Math.exp(-d * d * 3.2); g.fillStyle = lg; g.fillRect(0, y, W, 1) }
  g.globalAlpha = 1; return Texture.from(c)
}
// radial vignette: transparent centre -> dark edge.
function vignTex(): Texture {
  const S = 256, c = document.createElement('canvas'); c.width = c.height = S
  const g = c.getContext('2d')!, r = S / 2, grd = g.createRadialGradient(r, r, r * 0.12, r, r, r)
  grd.addColorStop(0, 'rgba(3,1,6,0)'); grd.addColorStop(0.7, 'rgba(3,1,6,0.5)'); grd.addColorStop(1, 'rgba(3,1,6,1)')
  g.fillStyle = grd; g.fillRect(0, 0, S, S); return Texture.from(c)
}

type Spark = { x: number; y: number; vx: number; vy: number; r: number; life: number; decay: number; tw: number; ph: number; tr: number[][]; sp: Sprite }
type Ray = { a: number; len: number; max: number; w: number; life: number; sp: number; spr: Sprite }
type Smoke = { x: number; y: number; vx: number; vy: number; r: number; gr: number; life: number; dec: number; spr: Sprite }

export function createFbRenderer(mount: ShadowRoot | HTMLElement) {
  const canvas = document.createElement('canvas')
  canvas.style.cssText = 'position:fixed;inset:0;width:100vw;height:100vh;z-index:94;pointer-events:none;display:none'
  mount.appendChild(canvas)

  const app = new Application()
  let ready = false, disposed = false, playing = false, t0 = 0, lastT = 0, dur = 5000
  let W = window.innerWidth || 1, H = window.innerHeight || 1
  let dark: Graphics, vign: Sprite, gfx: Graphics
  let smokeC: Container, addC: Container
  let core: Sprite[] = [], rays: Ray[] = [], sparks: Spark[] = [], embers: Spark[] = [], smokes: Smoke[] = []
  let ringR = 0

  function mkGlow(tex: Texture, parent: Container, add: boolean): Sprite {
    const s = new Sprite(tex); s.anchor.set(0.5); s.visible = false; if (add) s.blendMode = 'add'; parent.addChild(s); return s
  }

  app.init({ canvas, backgroundAlpha: 0, antialias: true, autoStart: false, resolution: window.devicePixelRatio || 1, width: W, height: H, preference: 'webgpu' })
    .then(() => {
      if (disposed) { try { app.destroy({ removeView: true }, { children: true, texture: true }) } catch (e) {} ; return }
      const gtex = glowTex(), stex = streakTex()
      dark = new Graphics()
      vign = new Sprite(vignTex())
      smokeC = new Container(); addC = new Container()
      gfx = new Graphics(); gfx.blendMode = 'add'
      app.stage.addChild(dark, vign, smokeC, gfx, addC) // dim < vignette < smoke < ring/trails < glows

      for (let i = 0; i < N_SMOKE; i++) smokes.push({ x: 0, y: 0, vx: 0, vy: 0, r: 0, gr: 0, life: 0, dec: 0, spr: mkGlow(gtex, smokeC, false) })
      core = [mkGlow(gtex, addC, true), mkGlow(gtex, addC, true), mkGlow(gtex, addC, true)]
      for (let i = 0; i < N_RAY; i++) { const spr = new Sprite(stex); spr.anchor.set(0, 0.5); spr.visible = false; spr.blendMode = 'add'; addC.addChild(spr); rays.push({ a: 0, len: 0, max: 0, w: 0, life: 0, sp: 0, spr }) }
      for (let i = 0; i < N_SPARK; i++) sparks.push({ x: 0, y: 0, vx: 0, vy: 0, r: 0, life: 0, decay: 0, tw: 0, ph: 0, tr: [], sp: mkGlow(gtex, addC, true) })
      for (let i = 0; i < N_EMBER; i++) embers.push({ x: 0, y: 0, vx: 0, vy: 0, r: 0, life: 0, decay: 0, tw: 0, ph: 0, tr: [], sp: mkGlow(gtex, addC, true) })
      ready = true
    })

  const rnd = (a: number, b: number) => a + Math.random() * (b - a)
  function emit() {
    const cx = W / 2, cy = H * 0.48, vmin = Math.min(W, H)
    ringR = 0
    for (let i = 0; i < N_SMOKE; i++) { const a = rnd(0, 6.283), sp = rnd(.2, .8) * vmin, s = smokes[i]; s.x = cx; s.y = cy; s.vx = Math.cos(a) * sp; s.vy = Math.sin(a) * sp; s.r = rnd(.05, .12) * vmin; s.gr = rnd(.18, .32) * vmin; s.life = 1; s.dec = rnd(.5, .9) }
    for (let i = 0; i < N_RAY; i++) { const r = rays[i]; r.a = rnd(0, 6.283); r.len = 0; r.max = rnd(.5, .95) * vmin * (i < 3 ? 1.25 : 1); r.w = rnd(.004, .012) * vmin; r.life = 1; r.sp = rnd(7, 12) }
    for (let i = 0; i < N_SPARK; i++) { const a = rnd(0, 6.283), sp = rnd(.4, 1) * 3.4 * vmin * (i < 10 ? 1.4 : 1), s = sparks[i]; s.x = cx; s.y = cy; s.vx = Math.cos(a) * sp; s.vy = Math.sin(a) * sp; s.r = rnd(.0025, .008) * vmin * (i < 10 ? 1.7 : 1); s.life = 1; s.decay = rnd(.7, 1.3); s.tw = rnd(6, 16); s.ph = rnd(0, 6.283); s.tr = [] }
    for (let i = 0; i < N_EMBER; i++) { const a = rnd(0, 6.283), sp = rnd(.15, .55) * vmin, s = embers[i]; s.x = cx; s.y = cy; s.vx = Math.cos(a) * sp; s.vy = Math.sin(a) * sp; s.r = rnd(.003, .007) * vmin; s.life = 1; s.decay = rnd(.32, .5); s.tw = rnd(4, 9); s.ph = rnd(0, 6.283); s.tr = [] }
  }

  // a glow sprite displays radius R (texture radius = GS/2) => scale = 2R/GS
  const setGlow = (s: Sprite, x: number, y: number, R: number, col: number, a: number) => {
    if (a <= 0.002 || R <= 0) { s.visible = false; return }
    s.visible = true; s.position.set(x, y); s.tint = col; s.alpha = a; s.scale.set((2 * R) / GS)
  }

  function draw(t: number, dt: number) {
    const cx = W / 2, cy = H * 0.48, vmin = Math.min(W, H), tn = t * 1000 / dur
    // focus dim + vignette (normal blend)
    const dk = clamp01(t < .05 ? t / .05 : tn > 0.82 ? 1 - (tn - 0.82) / 0.18 : 1) * 0.62
    dark.clear(); dark.rect(0, 0, W, H).fill({ color: 0x080208, alpha: dk * 0.5 })
    vign.position.set(0, 0); vign.width = W; vign.height = H; vign.alpha = dk * 0.7
    // smoke (normal, dark-red, expanding)
    for (const s of smokes) {
      s.vx *= 0.94; s.vy *= 0.94; s.x += s.vx * dt; s.y += s.vy * dt; s.r = lerp(s.r, s.gr, dt * 1.4); s.life -= dt * s.dec * 0.5
      setGlow(s.spr, s.x, s.y, s.r, 0x3c0c12, Math.max(0, s.life) * 0.18)
    }
    // shockwave ring (additive: soft wide + bright thin)
    gfx.clear()
    ringR += dt * 2.3 * vmin; const ra = clamp01(1 - t / 0.55)
    if (ra > 0.01) {
      gfx.circle(cx, cy, Math.max(0.1, ringR)).stroke({ width: Math.max(1, vmin * 0.02 * ra), color: hex(RINGC), alpha: ra * 0.5 })
      gfx.circle(cx, cy, Math.max(0.1, ringR)).stroke({ width: Math.max(1, vmin * 0.005 * ra), color: hex(HOT), alpha: ra * 0.9 })
    }
    // core flash -> bloom (additive glow layers)
    const cv = Math.max(0, 1 - Math.max(0, t - 0.04) / 0.9)
    const cr = vmin * (0.05 + 0.14 * clamp01(t * 7)) * (1 + t * 0.1)
    setGlow(core[0], cx, cy, cr * 2.2, hex(COOL), cv * 0.55)
    setGlow(core[1], cx, cy, cr, hex(MID), cv * 0.9)
    setGlow(core[2], cx, cy, cr * 0.5, hex(HOT), Math.max(0, 1 - t / 0.25))
    // light rays (streak sprites: base at centre, stretched along angle)
    for (const r of rays) {
      r.len = lerp(r.len, r.max, dt * r.sp); r.life -= dt * 1.5
      if (r.life <= 0) { r.spr.visible = false; continue }
      const a = r.life * r.life, base = cr * 0.6
      r.spr.visible = true; r.spr.position.set(cx + Math.cos(r.a) * base, cy + Math.sin(r.a) * base)
      r.spr.rotation = r.a; r.spr.alpha = a; r.spr.scale.set(Math.max(1, r.len - base) / 256, (r.w * (0.4 + r.life) * 2) / 48)
    }
    // sparks (colour-temperature + twinkle + trail)
    for (const s of sparks) {
      s.vx *= 0.9; s.vy *= 0.92; s.x += s.vx * dt; s.y += s.vy * dt; s.life -= dt * s.decay
      if (s.life <= 0) { s.sp.visible = false; continue }
      s.tr.push([s.x, s.y]); if (s.tr.length > 5) s.tr.shift()
      const tw = 0.7 + 0.3 * Math.sin(t * s.tw + s.ph), col = tempHex(s.life * 0.95), a = s.life * tw
      if (s.tr.length > 1) { gfx.moveTo(s.tr[0][0], s.tr[0][1]); for (let j = 1; j < s.tr.length; j++) gfx.lineTo(s.tr[j][0], s.tr[j][1]); gfx.stroke({ width: Math.max(0.4, s.r * 1.1), color: col, alpha: a * 0.5 }) }
      setGlow(s.sp, s.x, s.y, s.r * 3.2, col, a * 0.9)
    }
    // embers (lingering, gravity, twinkle)
    for (const e of embers) {
      e.vx *= 0.95; e.vy = e.vy * 0.95 + vmin * 0.08 * dt; e.x += e.vx * dt; e.y += e.vy * dt; e.life -= dt * e.decay
      if (e.life <= 0) { e.sp.visible = false; continue }
      const tw = 0.5 + 0.5 * Math.sin(t * e.tw + e.ph)
      setGlow(e.sp, e.x, e.y, e.r * 3.5, tempHex(e.life * 0.7), e.life * tw * 0.9)
    }
  }

  function frame() {
    if (disposed || !playing) return
    const now = performance.now(), t = (now - t0) / 1000
    const dt = Math.max(0, Math.min(0.05, (now - lastT) / 1000)); lastT = now
    if (t * 1000 >= dur) { playing = false; canvas.style.display = 'none'; return }
    draw(t, dt); app.render(); requestAnimationFrame(frame)
  }

  return {
    get ready() { return ready },
    play(durationMs = 5000) {
      if (!ready || disposed) return
      dur = durationMs
      W = window.innerWidth || 1; H = window.innerHeight || 1
      app.renderer.resize(W, H)
      emit()
      t0 = lastT = performance.now(); playing = true
      canvas.style.display = 'block'
      requestAnimationFrame(frame)
    },
    resize() { if (!ready || disposed) return; W = window.innerWidth || 1; H = window.innerHeight || 1; app.renderer.resize(W, H) },
    destroy() {
      disposed = true
      try { if (ready) app.destroy({ removeView: true }, { children: true, texture: true }) } catch (e) {}
      try { canvas.remove() } catch (e) {}
    },
  }
}
