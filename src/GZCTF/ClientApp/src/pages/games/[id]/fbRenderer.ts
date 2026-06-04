/**
 * PixiJS v8 renderer for the first-blood "slam" graphics — dark backdrop, the
 * red radial splash (x2), and the white flash. Moved off the DOM because those
 * were screen-sized layers (a complex SVG splash + a drop-shadow/mix-blend stack)
 * that rasterized huge CSS layers and composited over the live WebGL arena every
 * frame — the first-blood lag. Here the splash SVGs rasterize ONCE to small fixed
 * textures and the whole slam is GPU sprites/graphics on a single canvas.
 *
 * Full-viewport canvas at z-index 94 (just under the DOM FB text overlay, z-95).
 * play(ms) runs a self-timed timeline mirroring the old fbDark/fbSplat/fbSplat2/
 * fbFlash keyframes; the canvas is display:none except while playing.
 */
import { Application, Graphics, Sprite, Texture } from 'pixi.js'

const SPLAT1 = `data:image/svg+xml,%3Csvg%20xmlns='http://www.w3.org/2000/svg'%20viewBox='0%200%20600%20600'%3E%3Cdefs%3E%3CradialGradient%20id='g'%20cx='50%25'%20cy='50%25'%20r='58%25'%3E%3Cstop%20offset='0'%20stop-color='%23ff5a6e'/%3E%3Cstop%20offset='.35'%20stop-color='%23e8122f'/%3E%3Cstop%20offset='.72'%20stop-color='%23bf0f24'/%3E%3Cstop%20offset='1'%20stop-color='%238c0c1d'/%3E%3C/radialGradient%3E%3C/defs%3E%3Cg%20fill='url(%23g)'%3E%3Cellipse%20cx='300'%20cy='300'%20rx='52'%20ry='46'/%3E%3Cellipse%20cx='286'%20cy='310'%20rx='30'%20ry='26'/%3E%3Cellipse%20cx='316'%20cy='292'%20rx='26'%20ry='22'/%3E%3Cpath%20d='M340.2%20313.9%20Q450.7%20304.6%20541.1%20297%20Q450.6%20291.7%20339.8%20285.1%20Z'/%3E%3Cpath%20d='M333.8%20324.6%20Q436.8%20353.6%20521.1%20377.4%20Q440.4%20343.4%20341.7%20301.9%20Z'/%3E%3Cpath%20d='M321.4%20336.1%20Q423.2%20418.1%20506.5%20485.3%20Q430.7%20409.7%20338.2%20317.4%20Z'/%3E%3Cpath%20d='M302.9%20340.7%20Q338.4%20450.3%20367.5%20539.9%20Q345.6%20448.3%20318.8%20336.3%20Z'/%3E%3Cpath%20d='M291.8%20339.8%20Q293.7%20425.2%20295.3%20495%20Q300.2%20425.3%20306.3%20340.2%20Z'/%3E%3Cpath%20d='M270.3%20329%20Q216.5%20433.1%20172.5%20518.3%20Q225.1%20438.1%20289.3%20340.1%20Z'/%3E%3Cpath%20d='M258.3%20307.1%20Q134.2%20383.3%2032.7%20445.6%20Q140.1%20394.1%20271.4%20331.2%20Z'/%3E%3Cpath%20d='M258%20297.8%20Q190.7%20323.4%20135.7%20344.5%20Q193.8%20334.9%20264.8%20323.1%20Z'/%3E%3Cpath%20d='M264.4%20280.5%20Q136.7%20241%2032.3%20208.7%20Q134.7%20247%20259.9%20293.7%20Z'/%3E%3Cpath%20d='M276%20267.1%20Q201.9%20203.3%20141.2%20151.1%20Q197.2%20208.3%20265.6%20278.2%20Z'/%3E%3Cpath%20d='M288.3%20259%20Q227.2%20189.1%20177.2%20131.9%20Q216.5%20196.9%20264.5%20276.4%20Z'/%3E%3Cpath%20d='M304.8%20258.1%20Q283%20190.4%20265.1%20135%20Q271.2%20192.9%20278.6%20263.6%20Z'/%3E%3Cpath%20d='M317.1%20263%20Q341.6%20140.9%20361.6%2041.1%20Q334.5%20139.3%20301.4%20259.2%20Z'/%3E%3Cpath%20d='M336.2%20277.4%20Q383.3%20203.2%20421.8%20142.5%20Q372.8%20195.1%20312.8%20259.3%20Z'/%3E%3Cpath%20d='M341.8%20293.2%20Q424%20240.3%20491.2%20197%20Q418.1%20229.3%20328.7%20268.9%20Z'/%3E%3C/g%3E%3Cg%20fill='%23ff3b5b'%3E%3Ccircle%20cx='547.8'%20cy='296.9'%20r='6.9'/%3E%3Ccircle%20cx='530.4'%20cy='378.4'%20r='5.5'/%3E%3Ccircle%20cx='508.3'%20cy='493.2'%20r='8.8'/%3E%3Ccircle%20cx='368.1'%20cy='547'%20r='4.3'/%3E%3Ccircle%20cx='295.1'%20cy='501.4'%20r='8.2'/%3E%3Ccircle%20cx='168.1'%20cy='524'%20r='5.4'/%3E%3Ccircle%20cx='24.8'%20cy='448.1'%20r='5.1'/%3E%3Ccircle%20cx='129.9'%20cy='347.2'%20r='5.9'/%3E%3Ccircle%20cx='28.4'%20cy='205.2'%20r='6.3'/%3E%3Ccircle%20cx='135.2'%20cy='144.5'%20r='5.3'/%3E%3Ccircle%20cx='171.5'%20cy='129.3'%20r='5.2'/%3E%3Ccircle%20cx='264.3'%20cy='127.5'%20r='6.2'/%3E%3Ccircle%20cx='363.6'%20cy='38'%20r='6.1'/%3E%3Ccircle%20cx='428'%20cy='138.5'%20r='8.4'/%3E%3Ccircle%20cx='498.6'%20cy='195.6'%20r='8.9'/%3E%3Ccircle%20cx='333'%20cy='440.2'%20r='5.1'/%3E%3Ccircle%20cx='228'%20cy='433.1'%20r='2.3'/%3E%3Ccircle%20cx='473.6'%20cy='410.4'%20r='3'/%3E%3Ccircle%20cx='166.8'%20cy='201.6'%20r='3.8'/%3E%3Ccircle%20cx='480.8'%20cy='252.5'%20r='4.3'/%3E%3Ccircle%20cx='386.7'%20cy='203.5'%20r='2.6'/%3E%3Ccircle%20cx='510.1'%20cy='163.8'%20r='3'/%3E%3Ccircle%20cx='387'%20cy='518.8'%20r='5.8'/%3E%3Ccircle%20cx='390.7'%20cy='560.2'%20r='5.5'/%3E%3Ccircle%20cx='160.7'%20cy='194'%20r='2.4'/%3E%3Ccircle%20cx='569.7'%20cy='366.9'%20r='3'/%3E%3Ccircle%20cx='259.5'%20cy='162'%20r='5.3'/%3E%3Ccircle%20cx='176.1'%20cy='214.1'%20r='2.7'/%3E%3Ccircle%20cx='280'%20cy='193.8'%20r='2.9'/%3E%3Ccircle%20cx='60.7'%20cy='206.4'%20r='4.5'/%3E%3Ccircle%20cx='249.2'%20cy='564.5'%20r='2.8'/%3E%3Ccircle%20cx='445.4'%20cy='315.2'%20r='3.8'/%3E%3Ccircle%20cx='419.3'%20cy='347.6'%20r='3.5'/%3E%3Ccircle%20cx='192.1'%20cy='247.4'%20r='3.4'/%3E%3Ccircle%20cx='517.8'%20cy='122'%20r='4.6'/%3E%3Ccircle%20cx='225.6'%20cy='107.8'%20r='2.6'/%3E%3Ccircle%20cx='396'%20cy='321.5'%20r='5.6'/%3E%3Ccircle%20cx='215.7'%20cy='35.2'%20r='2.1'/%3E%3Ccircle%20cx='177.6'%20cy='159.1'%20r='4.9'/%3E%3Ccircle%20cx='180.5'%20cy='558.6'%20r='2.3'/%3E%3Ccircle%20cx='74.8'%20cy='232.9'%20r='5.6'/%3E%3C/g%3E%3Ccircle%20cx='300'%20cy='300'%20r='20'%20fill='%23ff7283'/%3E%3C/svg%3E`
const SPLAT2 = `data:image/svg+xml,%3Csvg%20xmlns='http://www.w3.org/2000/svg'%20viewBox='0%200%20600%20600'%3E%3Cg%20fill='%23e8122f'%3E%3Ccircle%20cx='107.3'%20cy='239.9'%20r='6.3'/%3E%3Ccircle%20cx='245.6'%20cy='519.9'%20r='4'/%3E%3Ccircle%20cx='44.7'%20cy='449.9'%20r='3'/%3E%3Ccircle%20cx='127.3'%20cy='362.6'%20r='4.1'/%3E%3Ccircle%20cx='499.4'%20cy='298.4'%20r='5.2'/%3E%3Ccircle%20cx='72.8'%20cy='425.6'%20r='4.5'/%3E%3Ccircle%20cx='403.1'%20cy='488.8'%20r='4.3'/%3E%3Ccircle%20cx='362.2'%20cy='74'%20r='3.4'/%3E%3Ccircle%20cx='210.3'%20cy='127.1'%20r='4.8'/%3E%3Ccircle%20cx='339.8'%20cy='507.8'%20r='6'/%3E%3Ccircle%20cx='38.4'%20cy='274.1'%20r='5.9'/%3E%3Ccircle%20cx='89.3'%20cy='171.6'%20r='4.3'/%3E%3Ccircle%20cx='103.3'%20cy='120'%20r='2.9'/%3E%3Ccircle%20cx='489.5'%20cy='221.7'%20r='3.1'/%3E%3Ccircle%20cx='418.2'%20cy='462.7'%20r='2.8'/%3E%3Ccircle%20cx='130'%20cy='427.1'%20r='5.4'/%3E%3Ccircle%20cx='16.1'%20cy='360.5'%20r='5.5'/%3E%3Ccircle%20cx='178.6'%20cy='553.7'%20r='5.6'/%3E%3Ccircle%20cx='554.7'%20cy='247'%20r='6.8'/%3E%3Ccircle%20cx='353.2'%20cy='484.2'%20r='6.9'/%3E%3Ccircle%20cx='585.8'%20cy='339.5'%20r='6.6'/%3E%3Ccircle%20cx='413.4'%20cy='430.1'%20r='6.6'/%3E%3C/g%3E%3Cg%20fill='%23ff5a6e'%3E%3Ccircle%20cx='534'%20cy='239.5'%20r='3.8'/%3E%3Ccircle%20cx='310'%20cy='137.2'%20r='2'/%3E%3Ccircle%20cx='131'%20cy='124.7'%20r='4.4'/%3E%3Ccircle%20cx='179.4'%20cy='288.7'%20r='2.4'/%3E%3Ccircle%20cx='155.4'%20cy='347.9'%20r='2.1'/%3E%3Ccircle%20cx='366.9'%20cy='477.8'%20r='2.3'/%3E%3Ccircle%20cx='408.7'%20cy='393.2'%20r='2.1'/%3E%3Ccircle%20cx='350.4'%20cy='64.1'%20r='2.5'/%3E%3Ccircle%20cx='138.4'%20cy='119'%20r='2.6'/%3E%3Ccircle%20cx='178.2'%20cy='510.7'%20r='3.9'/%3E%3Ccircle%20cx='132.7'%20cy='140.5'%20r='3.4'/%3E%3Ccircle%20cx='214.4'%20cy='498.3'%20r='4'/%3E%3C/g%3E%3C/svg%3E`

// Rasterize an SVG data-uri to a fixed-size texture (small + crisp, GPU-scaled after).
async function loadTex(uri: string, size = 720): Promise<Texture> {
  const img = new Image(); img.src = uri
  try { await img.decode() } catch (e) { await new Promise<void>(r => { img.onload = () => r(); img.onerror = () => r() }) }
  const c = document.createElement('canvas'); c.width = c.height = size
  c.getContext('2d')!.drawImage(img, 0, 0, size, size)
  return Texture.from(c)
}

// Piecewise-linear keyframe lookup. stops = [[t,v],...] ascending in t (0..1).
function kf(t: number, stops: number[][]): number {
  if (t <= stops[0][0]) return stops[0][1]
  for (let i = 1; i < stops.length; i++) {
    if (t <= stops[i][0]) {
      const a = stops[i - 1], b = stops[i]
      return a[1] + (b[1] - a[1]) * ((t - a[0]) / (b[0] - a[0] || 1))
    }
  }
  return stops[stops.length - 1][1]
}

export function createFbRenderer(mount: ShadowRoot | HTMLElement) {
  const canvas = document.createElement('canvas')
  canvas.style.cssText = 'position:fixed;inset:0;width:100vw;height:100vh;z-index:94;pointer-events:none;display:none'
  mount.appendChild(canvas)

  const app = new Application()
  let ready = false, disposed = false, playing = false, t0 = 0, dur = 5000
  let W = window.innerWidth || 1, H = window.innerHeight || 1, tw = 720
  let dark: Graphics, flash: Graphics, sp1: Sprite, sp2: Sprite

  app.init({ canvas, backgroundAlpha: 0, antialias: true, autoStart: false, resolution: window.devicePixelRatio || 1, width: W, height: H, preference: 'webgpu' })
    .then(async () => {
      if (disposed) { try { app.destroy({ removeView: true }, { children: true, texture: true }) } catch (e) {} ; return }
      const [t1, t2] = await Promise.all([loadTex(SPLAT1), loadTex(SPLAT2)])
      tw = t1.width || 720
      dark = new Graphics(); flash = new Graphics()
      sp1 = new Sprite(t1); sp1.anchor.set(0.5)
      sp2 = new Sprite(t2); sp2.anchor.set(0.5)
      app.stage.addChild(dark, sp2, sp1, flash) // dark < splat2 < splat1 < flash
      ready = true
    })

  function draw(t: number) {
    const cx = W / 2, cy = H * 0.47, vmin = Math.min(W, H)
    const da = kf(t, [[0, 0], [0.05, 0.95], [0.82, 0.95], [1, 0]])
    dark.clear(); dark.rect(0, 0, W, H).fill({ color: 0x0a0308, alpha: da })
    const s1 = kf(t, [[0, 0], [0.16, 1.12], [0.24, 0.97], [0.30, 1], [0.78, 1.03], [1, 1.14]])
    const a1 = kf(t, [[0, 0], [0.11, 0], [0.16, 1], [0.78, 1], [1, 0]])
    sp1.position.set(cx, cy); sp1.scale.set((1.08 * vmin) / tw * s1); sp1.alpha = a1
    const s2 = kf(t, [[0, 0], [0.21, 1.08], [0.30, 1], [0.78, 1.04], [1, 1.16]])
    const a2 = kf(t, [[0, 0], [0.15, 0], [0.21, 0.9], [0.30, 0.85], [0.78, 0.8], [1, 0]])
    sp2.position.set(cx, cy); sp2.scale.set((1.26 * vmin) / tw * s2); sp2.alpha = a2
    const fa = kf(t, [[0, 0], [0.11, 0], [0.13, 0.95], [0.16, 0], [0.18, 0.5], [0.21, 0], [1, 0]])
    flash.clear(); if (fa > 0.001) flash.rect(0, 0, W, H).fill({ color: 0xffffff, alpha: fa })
  }

  function frame() {
    if (disposed || !playing) return
    const t = (performance.now() - t0) / dur
    if (t >= 1) { playing = false; draw(1); app.render(); canvas.style.display = 'none'; return }
    draw(t); app.render(); requestAnimationFrame(frame)
  }

  return {
    get ready() { return ready },
    play(durationMs = 5000) {
      if (!ready || disposed) return
      dur = durationMs; t0 = performance.now(); playing = true
      W = window.innerWidth || 1; H = window.innerHeight || 1
      app.renderer.resize(W, H)
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
