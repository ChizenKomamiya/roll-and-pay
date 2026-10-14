import { describe, expect, it } from 'vitest'
import { Euler, Vector3 } from 'three'
import { FACE_VALUES, faceRotation } from './dice-view'

describe('出目と3D上面の一致', () => {
  const normals = [new Vector3(1, 0, 0), new Vector3(-1, 0, 0), new Vector3(0, 1, 0), new Vector3(0, -1, 0), new Vector3(0, 0, 1), new Vector3(0, 0, -1)]
  it.each([1, 2, 3, 4, 5, 6])('出目%dの面の法線が上を向く', value => {
    const rotation = new Euler(...faceRotation(value))
    const normal = normals[FACE_VALUES.findIndex(face => face === value)].clone().applyEuler(rotation)
    expect(normal.y).toBeCloseTo(1)
    expect(normal.x).toBeCloseTo(0)
    expect(normal.z).toBeCloseTo(0)
  })
})
