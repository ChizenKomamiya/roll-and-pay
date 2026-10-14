import * as THREE from 'three'
import { gsap } from 'gsap'
import { el } from './ui'

// BoxGeometry material order: +X, -X, +Y, -Y, +Z, -Z.
export const FACE_VALUES = [1, 6, 2, 5, 3, 4] as const
export function faceRotation(value: number): [number, number, number] {
  switch (value) {
    case 1: return [0, 0, Math.PI / 2]
    case 6: return [0, 0, -Math.PI / 2]
    case 2: return [0, 0, 0]
    case 5: return [Math.PI, 0, 0]
    case 3: return [-Math.PI / 2, 0, 0]
    case 4: return [Math.PI / 2, 0, 0]
    default: throw new Error('出目は1〜6です。')
  }
}

function faceTexture(value: number) {
  const canvas = document.createElement('canvas'); canvas.width = canvas.height = 128
  const context = canvas.getContext('2d')!
  context.fillStyle = '#fffdf7'; context.fillRect(0, 0, 128, 128)
  context.strokeStyle = '#d4ddcb'; context.lineWidth = 6; context.strokeRect(3, 3, 122, 122)
  const points: Record<number, number[][]> = {
    1: [[64, 64]], 2: [[32, 32], [96, 96]], 3: [[32, 32], [64, 64], [96, 96]],
    4: [[32, 32], [96, 32], [32, 96], [96, 96]],
    5: [[32, 32], [96, 32], [64, 64], [32, 96], [96, 96]],
    6: [[32, 28], [96, 28], [32, 64], [96, 64], [32, 100], [96, 100]],
  }
  context.fillStyle = '#206e55'
  points[value].forEach(([x, y]) => { context.beginPath(); context.arc(x, y, 10, 0, Math.PI * 2); context.fill() })
  const texture = new THREE.CanvasTexture(canvas); texture.colorSpace = THREE.SRGBColorSpace
  return texture
}

export interface DiceView { element: HTMLElement; play: (value: number) => Promise<void>; dispose: () => void }
export function diceView(value: number | null): DiceView {
  const host = el('div', 'dice-view'), fallback = el('span', 'die-fallback', value === null ? '?' : String(value))
  host.setAttribute('aria-hidden', 'true')
  let renderer: THREE.WebGLRenderer
  try { renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true }) } catch {
    host.append(fallback)
    return { element: host, async play(next) { fallback.textContent = String(next) }, dispose() {} }
  }
  renderer.setSize(112, 112); renderer.setPixelRatio(Math.min(devicePixelRatio, 2))
  const scene = new THREE.Scene(), camera = new THREE.PerspectiveCamera(32, 1, 0.1, 100)
  camera.position.set(2.6, 3.8, 4); camera.lookAt(0, 0, 0)
  scene.add(new THREE.AmbientLight(0xffffff, 1.3))
  const light = new THREE.DirectionalLight(0xffffff, 2); light.position.set(3, 5, 4); scene.add(light)
  const textures = FACE_VALUES.map(faceTexture)
  const materials = textures.map(map => new THREE.MeshStandardMaterial({ map, roughness: 0.7 }))
  const geometry = new THREE.BoxGeometry(1.6, 1.6, 1.6), cube = new THREE.Mesh(geometry, materials)
  scene.add(cube)
  host.append(renderer.domElement)
  let alive = true, complete: (() => void) | undefined
  function render() { if (alive) renderer.render(scene, camera) }
  if (value !== null) cube.rotation.set(...faceRotation(value))
  else { cube.visible = false; host.append(fallback) }
  render()
  return {
    element: host,
    async play(next) {
      if (!alive) return
      fallback.remove(); cube.visible = true
      const [x, y, z] = faceRotation(next)
      if (matchMedia('(prefers-reduced-motion: reduce)').matches) { cube.rotation.set(x, y, z); render(); return }
      await new Promise<void>(resolve => {
        complete = resolve
        gsap.to(cube.rotation, { x: x + Math.PI * 6, y: y + Math.PI * 4, z: z + Math.PI * 4, duration: 0.95, ease: 'power2.out', onUpdate: render, onComplete: () => { cube.rotation.set(x, y, z); render(); complete = undefined; resolve() } })
      })
    },
    dispose() {
      if (!alive) return
      alive = false; gsap.killTweensOf(cube.rotation); complete?.(); complete = undefined
      geometry.dispose(); materials.forEach(material => material.dispose()); textures.forEach(texture => texture.dispose()); renderer.dispose(); renderer.forceContextLoss()
    },
  }
}
