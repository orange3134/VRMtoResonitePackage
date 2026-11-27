"""Meter-space geometry for Unity transforms and saved Resonite slots."""
import math


def add(a, b): return [x + y for x, y in zip(a, b)]
def sub(a, b): return [x - y for x, y in zip(a, b)]
def mul(a, s): return [x * s for x in a]
def dot(a, b): return sum(x * y for x, y in zip(a, b))
def cross(a, b): return [a[1]*b[2]-a[2]*b[1], a[2]*b[0]-a[0]*b[2], a[0]*b[1]-a[1]*b[0]]


def unit(a):
    length = math.sqrt(dot(a, a))
    if length < 1e-8:
        raise ValueError('Degenerate humanoid reference frame')
    return mul(a, 1 / length)


def frame(anchors):
    x = unit(sub(anchors['RightUpperArm'], anchors['LeftUpperArm']))
    up = sub(anchors['Head'], anchors['Hips'])
    y = unit(sub(up, mul(x, dot(up, x))))
    return x, y, unit(cross(x, y))


def values(v, axes):
    return [v[k] for k in axes] if isinstance(v, dict) else v


def world(slots, sid, point=None, *, rotation_only=False):
    point = [0, 0, 0] if point is None else point
    while sid:
        slot = slots[sid]
        q = values(slot['localRotation'], 'xyzw')
        if abs(dot(q, q) - 1) > 1e-4:
            raise ValueError('Rotation is not a unit quaternion')
        if not rotation_only:
            point = [v * s for v, s in zip(point, values(slot['localScale'], 'xyz'))]
        t = mul(cross(q[:3], point), 2)
        point = add(add(point, mul(t, q[3])), cross(q[:3], t))
        if not rotation_only:
            point = add(point, values(slot['localPosition'], 'xyz'))
        sid = slot['parent']
    return point
