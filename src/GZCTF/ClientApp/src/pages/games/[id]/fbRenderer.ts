/**
 * PixiJS v8 renderer for the first-blood "SUPERNOVA" slam — a GPU particle burst
 * from the centre: a bright additive core bloom, ~90 thin fast ray-tendrils flung
 * outward, an expanding shock ring, and an impact flash, all over a dark backdrop.
 *
 * Replaces the old DOM/CSS splash (screen-sized SVG layers composited over the
 * live WebGL arena every frame — the first-blood lag). Full-viewport canvas at
 * z-index 94 (just under the DOM FB text overlay, z-95). play(ms) fires a
 * self-timed burst; the canvas is display:none except while playing. Prefers
 * WebGPU (auto WebGL fallback), like the FX/jeopardy renderers.
 */
import { Application, Graphics, Sprite, Texture } from 'pixi.js'

// SUPERNOVA tuning — ratios from the chosen sample, scaled to the viewport's vmin.
const COUNT = 90          // ray particles
const SP0 = 1.36, SP1 = 3.7   // initial speed as a multiple of vmin per second
const R0 = 0.0036, R1 = 0.0118 // ray half-width as a multiple of vmin
const DRAG = 0.88         // velocity retained per 60fps-frame (decelerating rays)
const FADE = 1.0          // life drained per second (~1s rays)
const RING_SP = 2.0       // shock-ring growth: vmin per second
const BLOB_R = 0.16       // core-bloom radius as a multiple of vmin
const TENDRIL = 14        // trail points per ray streak

// Soft radial bloom texture (white-hot core → red → transparent), built once.
function blobTexture(size = 256): Texture {
  const c = document.createElement('canvas'); c.width = c.height = size
  const g = c.getContext('2d')!, r = size / 2
  const grd = g.createRadialGradient(r, r, 0, r, r, r)
  grd.addColorStop(0, 'rgba(255,120,135,1)')   // sample bloom: pink-red core (NOT white-hot)
  grd.addColorStop(0.4, 'rgba(232,18,47,0.95)')
  grd.addColorStop(1, 'rgba(140,12,29,0)')
  g.fillStyle = grd; g.beginPath(); g.arc(r, r, r, 0, Math.PI * 2); g.fill()
  return Texture.from(c)
}

// Piecewise-linear keyframe lookup. stops = [[t,v],...] ascending in t.
function kf(t: number, stops: number[][]): number {
  if (t <= stops[0][0]) return stops[0][1]
  for (let i = 1; i < stops.length; i++) if (t <= stops[i][0]) {
    const a = stops[i - 1], b = stops[i]
    return a[1] + (b[1] - a[1]) * ((t - a[0]) / (b[0] - a[0] || 1))
  }
  return stops[stops.length - 1][1]
}

type Ray = { x: number; y: number; vx: number; vy: number; r: number; life: number; tr: number[][] }

export function createFbRenderer(mount: ShadowRoot | HTMLElement) {
  const canvas = document.createElement('canvas')
  canvas.style.cssText = 'position:fixed;inset:0;width:100vw;height:100vh;z-index:94;pointer-events:none;display:none'
  mount.appendChild(canvas)

  const app = new Application()
  let ready = false, disposed = false, playing = false, t0 = 0, lastT = 0, dur = 5000
  let W = window.innerWidth || 1, H = window.innerHeight || 1
  let dark: Graphics, gfx: Graphics, flash: Graphics, blob: Sprite
  let rays: Ray[] = [], ring = 0

  app.init({ canvas, backgroundAlpha: 0, antialias: true, autoStart: false, resolution: window.devicePixelRatio || 1, width: W, height: H, preference: 'webgpu' })
    .then(() => {
      if (disposed) { try { app.destroy({ removeView: true }, { children: true, texture: true }) } catch (e) {} ; return }
      dark = new Graphics(); gfx = new Graphics(); flash = new Graphics()
      blob = new Sprite(blobTexture()); blob.anchor.set(0.5) // normal blend, like the sample bloom
      app.stage.addChild(dark, blob, gfx, flash) // dark < bloom < rays/ring < flash
      ready = true
    })

  function emit() {
    const cx = W / 2, cy = H * 0.47, vmin = Math.min(W, H)
    rays = []; ring = 0
    for (let i = 0; i < COUNT; i++) {
      const a = Math.random() * Math.PI * 2
      const sp = (SP0 + Math.random() * (SP1 - SP0)) * vmin * (i < 8 ? 1.4 : 1) // a few long flares
      rays.push({
        x: cx, y: cy, vx: Math.cos(a) * sp, vy: Math.sin(a) * sp,
        r: (R0 + Math.random() * (R1 - R0)) * vmin * (i < 8 ? 1.5 : 1), life: 1, tr: []
      })
    }
  }

  function draw(elapsed: number, dt: number) {
    const cx = W / 2, cy = H * 0.47, vmin = Math.min(W, H), tn = elapsed * 1000 / dur
    // dark backdrop — dims the board over the full slam, fades at the end
    const da = kf(tn, [[0, 0], [0.05, 0.92], [0.82, 0.92], [1, 0]])
    dark.clear(); dark.rect(0, 0, W, H).fill({ color: 0x0a0308, alpha: da })
    // additive core bloom — punches in then fades over ~1.5s
    const ba = Math.max(0, 1 - Math.max(0, elapsed - 0.35) / 1.5)
    const br = Math.max(1, vmin * BLOB_R * Math.min(1, elapsed * 6) * (1 + Math.max(0, elapsed - 0.2) * 0.18))
    blob.visible = ba > 0.01; blob.position.set(cx, cy); blob.alpha = ba; blob.scale.set((br * 2) / 256)
    // shock ring + ray streaks in one Graphics
    gfx.clear()
    ring += dt * RING_SP * vmin
    const ringA = Math.max(0, 1 - elapsed / 0.5)
    if (ringA > 0.01) gfx.circle(cx, cy, Math.max(0.1, ring)).stroke({ width: Math.max(1, 4 * ringA), color: 0xff5a6e, alpha: ringA * 0.7 })
    const dragF = Math.pow(DRAG, dt * 60)
    for (const p of rays) {
      p.vx *= dragF; p.vy *= dragF
      p.x += p.vx * dt; p.y += p.vy * dt; p.life -= dt * FADE
      if (p.life <= 0) continue
      p.tr.push([p.x, p.y]); if (p.tr.length > TENDRIL) p.tr.shift()
      const a = p.life
      // one reddish droplet colour for streak + head (sample brightens g/b slightly with life)
      const col = (245 << 16) | ((20 + 30 * a) | 0) << 8 | ((40 + 20 * a) | 0)
      if (p.tr.length > 1) {
        gfx.moveTo(p.tr[0][0], p.tr[0][1]); for (let j = 1; j < p.tr.length; j++) gfx.lineTo(p.tr[j][0], p.tr[j][1])
        gfx.stroke({ width: Math.max(0.5, p.r * 1.2), color: col, alpha: a })
      }
      gfx.circle(p.x, p.y, Math.max(0.5, p.r * (0.5 + 0.5 * a))).fill({ color: col, alpha: a })
    }
    // single impact flash, capped at 0.5 like the sample
    const fa = Math.max(0, 0.5 - elapsed * 3)
    flash.clear(); if (fa > 0.001) flash.rect(0, 0, W, H).fill({ color: 0xffffff, alpha: fa })
  }

  function frame() {
    if (disposed || !playing) return
    const now = performance.now()
    const elapsed = (now - t0) / 1000
    const dt = Math.max(0, Math.min(0.05, (now - lastT) / 1000)); lastT = now
    if (elapsed * 1000 >= dur) { playing = false; canvas.style.display = 'none'; return }
    draw(elapsed, dt); app.render(); requestAnimationFrame(frame)
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
