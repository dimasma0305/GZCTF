/**
 * PixiJS v8 WebGL renderer for the battle-arena FX layer (Phase 1 of the Pixi
 * migration). It renders the SAME shots[] / sparks[] / fxq[] arrays the engine
 * already owns — point FX (sparks + shot heads) become GPU particles in a
 * ParticleContainer, and the vector FX (shot trails, beams, hex shields, down
 * rings, expanding spark rings) are drawn into ONE batched Graphics per frame.
 *
 * It renders into its OWN <canvas> (overlaid on #fx) so the legacy 2D #fx canvas
 * stays a clean fallback: until app.init() resolves (ready=false), or if WebGL
 * init fails, the caller keeps drawing the 2D path. The 0..1000 logical space is
 * preserved by stage.scale = cssW/1000, so every coordinate in the engine is
 * unchanged. Driven by the engine's rAF loop (autoStart:false → render() per tick).
 */
import { Application, Container, Graphics, ParticleContainer, Particle, Rectangle, Texture } from 'pixi.js'

export interface FxRenderer {
  readonly ready: boolean
  tick(dt: number, shots: any[], sparks: any[], fxq: any[]): void
  resize(cssW: number, cssH: number): void
  destroy(): void
}

const colNum = (c: string): number => {
  if (typeof c !== 'string') return 0xffffff
  if (c[0] === '#') { const h = c.slice(1); return parseInt(h.length === 3 ? h.replace(/./g, '$&$&') : h, 16) }
  return 0xffffff
}

// a soft white radial dot, baked once into a texture (tinted per particle)
function bakeDot(): Texture {
  const c = document.createElement('canvas'); c.width = c.height = 64
  const g = c.getContext('2d')!
  const grd = g.createRadialGradient(32, 32, 0, 32, 32, 32)
  grd.addColorStop(0, 'rgba(255,255,255,1)'); grd.addColorStop(0.5, 'rgba(255,255,255,0.6)'); grd.addColorStop(1, 'rgba(255,255,255,0)')
  g.fillStyle = grd; g.fillRect(0, 0, 64, 64)
  return Texture.from(c)
}

export function createFxRenderer(refCanvas: HTMLCanvasElement): FxRenderer {
  const app = new Application()
  let ready = false, disposed = false, wasDirty = false // wasDirty: emit exactly one clearing frame when FX drain to empty
  // own canvas, overlaid exactly on the legacy #fx canvas
  const canvas = document.createElement('canvas')
  canvas.style.cssText = 'position:absolute;inset:0;width:100%;height:100%;pointer-events:none'
  refCanvas.parentNode?.insertBefore(canvas, refCanvas.nextSibling)

  let dot: Texture, sparksPC: ParticleContainer, headsPC: ParticleContainer, vec: Graphics
  const sparkPairs: { o: any; p: Particle }[] = []
  const headPairs: { o: any; p: Particle }[] = []

  const r0 = refCanvas.getBoundingClientRect()
  ;(async () => {
    await app.init({ canvas, backgroundAlpha: 0, antialias: true, autoStart: false, autoDensity: true, resolution: window.devicePixelRatio || 1, width: Math.max(r0.width, 1), height: Math.max(r0.height, 1), preference: 'webgl' })
    if (disposed) { app.destroy({ removeView: true }, { children: true, texture: true }); return }
    dot = bakeDot()
    const layer = new Container()
    vec = new Graphics()
    // position+color are mutated per frame; scale (vertex) is baked once at creation → static/fast.
    // boundsArea avoids Pixi recomputing bounds from every particle each frame.
    const bounds = new Rectangle(0, 0, 1000, 1000)
    sparksPC = new ParticleContainer({ dynamicProperties: { position: true, color: true }, boundsArea: bounds })
    headsPC = new ParticleContainer({ dynamicProperties: { position: true, color: true }, boundsArea: bounds })
    sparksPC.blendMode = headsPC.blendMode = vec.blendMode = 'add'
    layer.addChild(vec, sparksPC, headsPC) // trails/beams under the glowing points
    app.stage.addChild(layer)
    app.stage.scale.set(r0.width / 1000)
    refCanvas.style.display = 'none' // hand the layer over to Pixi; legacy 2D canvas hidden
    ready = true
  })().catch(() => { ready = false /* keep the 2D fallback alive */ })

  // Reconcile a Pixi ParticleContainer against a plain JS array each frame: create a
  // Particle for any array entry lacking a __p back-ref, drop particles whose entry died,
  // and let `update` mutate the survivors. Scale is baked at creation (constant per particle).
  function reconcile(arr: any[], pc: ParticleContainer, pairs: { o: any; p: Particle }[], makeScale: (o: any) => number, dead: (o: any) => boolean, update: (o: any, p: Particle) => void) {
    for (const o of arr) {
      if (o.__skip) continue
      if (!o.__p) { const sc = makeScale(o); const p = new Particle({ texture: dot, anchorX: 0.5, anchorY: 0.5, scaleX: sc, scaleY: sc }); pc.addParticle(p); o.__p = p; pairs.push({ o, p }) }
    }
    for (let i = pairs.length - 1; i >= 0; i--) {
      const { o, p } = pairs[i]
      if (dead(o)) { pc.removeParticle(p); o.__p = null; pairs.splice(i, 1); continue }
      update(o, p)
    }
  }

  function bez(a: number, c: number, b: number, t: number) { const u = 1 - t; return u * u * a + 2 * u * t * c + t * t * b }
  function hexPath(g: Graphics, x: number, y: number, r: number) { for (let i = 0; i < 6; i++) { const a = (60 * i - 90) * Math.PI / 180; const px = x + r * Math.cos(a), py = y + r * Math.sin(a); i ? g.lineTo(px, py) : g.moveTo(px, py) } g.closePath() }

  return {
    get ready() { return ready },
    resize(cssW: number, cssH: number) {
      if (!ready) return
      app.renderer.resize(Math.max(cssW, 1), Math.max(cssH, 1)); app.stage.scale.set(cssW / 1000)
    },
    destroy() {
      disposed = true
      try { if (ready) app.destroy({ removeView: true }, { children: true, texture: true }) } catch (e) {}
      try { canvas.remove() } catch (e) {}
    },
    tick(dt: number, shots: any[], sparks: any[], fxq: any[]) {
      if (!ready) return
      // Idle early-out: skip the per-frame WebGL submit when there's nothing to draw. Gate on the
      // PAIR lists too (not just the input arrays) — drawFX splices a dead shot/spark one frame before
      // reconcile prunes its particle, so this MUST stay busy until the pairs drain or the last
      // particle would freeze on screen. Emit exactly one clearing frame on the falling edge.
      const busy = shots.length || sparks.length || fxq.length || sparkPairs.length || headPairs.length
      if (!busy) { if (!wasDirty) return; wasDirty = false; vec.clear(); app.render(); return }
      wasDirty = true
      // ---- point FX: sparks (non-ring) + shot heads as particles ----
      sparks.forEach((sp: any) => (sp.__skip = sp.ring)) // ring sparks → vector, not particles
      reconcile(sparks, sparksPC, sparkPairs, () => 0.13, (o) => o.life <= 0,
        (o, p) => { p.x = o.x; p.y = o.y; p.tint = colNum(o.col); p.alpha = Math.max(o.life, 0) })
      shots.forEach((s: any) => { s.__hx = bez(s.fx, s.cx, s.tx, Math.min(s.t, 1)); s.__hy = bez(s.fy, s.cy, s.ty, Math.min(s.t, 1)) })
      reconcile(shots, headsPC, headPairs, (s) => (s.miss ? 0.16 : 0.32), (s) => s.t >= 1,
        (s, p) => { p.x = s.__hx; p.y = s.__hy; p.tint = colNum(s.col); p.alpha = s.miss ? 0.55 : 1 })

      // ---- vector FX: trails + beams + shields + downs + spark rings (one Graphics) ----
      vec.clear()
      for (const s of shots) {
        const tr = s.trail, n = tr.length; if (n < 2) continue
        const aMul = s.miss ? 0.32 : 0.9, wMul = s.miss ? 0.45 : 1
        vec.moveTo(tr[0].x, tr[0].y); for (let j = 1; j < n; j++) vec.lineTo(tr[j].x, tr[j].y)
        vec.stroke({ width: 3 * wMul, color: colNum(s.col), alpha: 0.38 * aMul })
        vec.moveTo(tr[n - 2].x, tr[n - 2].y).lineTo(tr[n - 1].x, tr[n - 1].y)
        vec.stroke({ width: 6 * wMul, color: colNum(s.col), alpha: 0.9 * aMul })
      }
      for (const sp of sparks) {
        if (!sp.ring) continue
        const col = colNum(sp.col), a = Math.max(sp.life, 0)
        vec.circle(sp.x, sp.y, Math.max(sp.r, 0.1)).stroke({ width: 3, color: col, alpha: a })
        for (let k = 0; k < 8; k++) { const ang = k / 8 * 6.28; vec.moveTo(sp.x + Math.cos(ang) * sp.r, sp.y + Math.sin(ang) * sp.r).lineTo(sp.x + Math.cos(ang) * (sp.r + 10), sp.y + Math.sin(ang) * (sp.r + 10)) }
        vec.stroke({ width: 1, color: col, alpha: a })
      }
      for (const e of fxq) {
        const p = Math.min(e.t, 1), col = colNum(e.col)
        if (e.kind === 'shield') {
          const rIn = 70 - 58 * Math.min(p * 2, 1)
          hexPath(vec, e.x, e.y, Math.max(rIn, 12)); vec.stroke({ width: 3, color: col, alpha: (1 - p) * 0.9 })
          hexPath(vec, e.x, e.y, 18 + 60 * p); vec.stroke({ width: 2, color: col, alpha: (1 - p) * 0.6 })
          hexPath(vec, e.x, e.y, Math.max(rIn, 12)); vec.fill({ color: col, alpha: (1 - p) * 0.28 })
        } else if (e.kind === 'down') {
          vec.circle(e.x, e.y, Math.max(70 * (1 - p), 2)).stroke({ width: 3, color: col, alpha: (1 - p) * 0.85 })
          for (let k = 0; k < 3; k++) { const yy = e.y + (k - 1) * 18; vec.rect(e.x - 30, yy, 60, 2).fill({ color: col, alpha: (1 - p) * 0.5 }) }
        } else if (e.kind === 'beam') {
          const tt = Math.min(p / 0.55, 1), hx = e.fx + (e.tx - e.fx) * tt, hy = e.fy + (e.ty - e.fy) * tt
          const w = (e.big ? 16 : 9) * (1 - p * 0.4), ga = Math.min(p * 3, 1) * (1 - Math.max(p - 0.7, 0) / 0.3)
          vec.moveTo(e.fx, e.fy).lineTo(hx, hy).stroke({ width: w * 2.2, color: col, alpha: ga * 0.22 })
          vec.moveTo(e.fx, e.fy).lineTo(hx, hy).stroke({ width: w, color: col, alpha: ga })
          vec.moveTo(e.fx, e.fy).lineTo(hx, hy).stroke({ width: w * 0.4, color: 0xffffff, alpha: ga })
        }
      }
      app.render()
    },
  }
}
