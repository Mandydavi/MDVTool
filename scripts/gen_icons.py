#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
重新生成 src/MDVTool.Desktop/Themes/Icons.xaml
做法：
 1) 从 docs/reference/全新设计-可交互原型-v2.html 的 const GLYPH 取出每个图标的原始 SVG 标记
 2) 把每个 SVG 元素(circle/rect/path)各自独立转换成【绝对坐标】的 path 'd'
    —— 关键点：原型里每个 <element> 都是独立坐标系（原点为 0,0），
       不能用单条 PathGeometry 直接拼接，否则第 2 个元素的相对命令会相对到上一个图形起点。
 3) 原型没有的文件类型图标(cube/archive/package/music/film/image 等)用手绘的干净绝对路径补上
 4) 统一描边线性风格（模板里只描边、不填色），与 v2 原型一致
"""
import re, os, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PROTOTYPE = os.path.join(ROOT, "docs/reference/全新设计-可交互原型-v2.html")
OUT = os.path.join(ROOT, "src/MDVTool.Desktop/Themes/Icons.xaml")

prototype = open(PROTOTYPE, encoding="utf-8").read()
m = re.search(r"const GLYPH=\{([\s\S]*?)\n\};", prototype)
assert m, "找不到 GLYPH"
GLYPH_BODY = m.group(1)

# 解析 const GLYPH={ ... } 里的 'name':'<svg...>' 或 name:'<svg...>'
proto_icons = {}
for key, val in re.findall(r"^\s*'?([\w-]+)'?\s*:\s*'([^']*)'", GLYPH_BODY, re.M):
    proto_icons[key] = val

# ---------------------------------------------------------------------------
# SVG 路径绝对化
# ---------------------------------------------------------------------------
import math

def _nums(it):
    return [float(x) for x in it]

def path_abs(d):
    """把一条 path 的 d 字符串（可能含相对命令）转成全绝对命令字符串。"""
    out = []
    cur = [0.0, 0.0]      # current point
    start = [0.0, 0.0]    # current subpath start
    prev_ctrl = None      # 上一个控制点（用于 S/T）
    last_cmd = None
    i = 0
    n = len(d)
    # 拆成 token：命令字母 + 数字序列
    toks = re.findall(r"([MmLlHhVvCcSsQqTtAaZz])|(-?\d*\.?\d+(?:e-?\d+)?)", d)
    # 重新组织：命令与跟在它后面的数字
    cmd = None
    params = []
    seq = []
    for ch, num in toks:
        if ch:
            if cmd is not None:
                seq.append((cmd, params))
            cmd = ch
            params = []
        else:
            params.append(float(num))
    if cmd is not None:
        seq.append((cmd, params))

    def emit_abs(c):
        out.append(c)

    for cmd, p in seq:
        c = cmd
        if c == 'M':
            x, y = p[0], p[1]
            cur = [x, y]; start = [x, y]
            out.append(f"M{x:g} {y:g}")
            # 后续成对参数视为 L
            for k in range(2, len(p), 2):
                x, y = p[k], p[k+1]
                cur = [x, y]
                out.append(f"L{x:g} {y:g}")
            prev_ctrl = None
        elif c == 'm':
            x, y = p[0] + cur[0], p[1] + cur[1]
            cur = [x, y]; start = [x, y]
            out.append(f"M{x:g} {y:g}")
            for k in range(2, len(p), 2):
                x, y = p[k] + cur[0], p[k+1] + cur[1]
                cur = [x, y]
                out.append(f"L{x:g} {y:g}")
            prev_ctrl = None
        elif c == 'L':
            x, y = p[0], p[1]
            cur = [x, y]; out.append(f"L{x:g} {y:g}")
            for k in range(2, len(p), 2):
                x, y = p[k], p[k+1]
                cur = [x, y]; out.append(f"L{x:g} {y:g}")
            prev_ctrl = None
        elif c == 'l':
            x, y = p[0] + cur[0], p[1] + cur[1]
            cur = [x, y]; out.append(f"L{x:g} {y:g}")
            for k in range(2, len(p), 2):
                x, y = p[k] + cur[0], p[k+1] + cur[1]
                cur = [x, y]; out.append(f"L{x:g} {y:g}")
            prev_ctrl = None
        elif c == 'H':
            x = p[0]; cur = [x, cur[1]]; out.append(f"L{x:g} {cur[1]:g}"); prev_ctrl = None
        elif c == 'h':
            x = p[0] + cur[0]; cur = [x, cur[1]]; out.append(f"L{x:g} {cur[1]:g}"); prev_ctrl = None
        elif c == 'V':
            y = p[0]; cur = [cur[0], y]; out.append(f"L{cur[0]:g} {y:g}"); prev_ctrl = None
        elif c == 'v':
            y = p[0] + cur[1]; cur = [cur[0], y]; out.append(f"L{cur[0]:g} {y:g}"); prev_ctrl = None
        elif c == 'C':
            for k in range(0, len(p), 6):
                x1,y1,x2,y2,x,y = p[k:k+6]
                cur=[x,y]; prev_ctrl=[x2,y2]
                out.append(f"C{x1:g} {y1:g} {x2:g} {y2:g} {x:g} {y:g}")
        elif c == 'c':
            for k in range(0, len(p), 6):
                x1,y1,x2,y2,x,y = p[k]+cur[0],p[k+1]+cur[1],p[k+2]+cur[0],p[k+3]+cur[1],p[k+4]+cur[0],p[k+5]+cur[1]
                cur=[x,y]; prev_ctrl=[x2,y2]
                out.append(f"C{x1:g} {y1:g} {x2:g} {y2:g} {x:g} {y:g}")
        elif c == 'S':
            for k in range(0, len(p), 4):
                x2,y2,x,y = p[k:k+4]
                # 反射控制点
                if prev_ctrl and last_cmd in ('C','c','S','s'):
                    rc = [2*cur[0]-prev_ctrl[0], 2*cur[1]-prev_ctrl[1]]
                else:
                    rc = cur[:]
                out.append(f"C{rc[0]:g} {rc[1]:g} {x2:g} {y2:g} {x:g} {y:g}")
                cur=[x,y]; prev_ctrl=[x2,y2]
        elif c == 's':
            for k in range(0, len(p), 4):
                x2,y2,x,y = p[k]+cur[0],p[k+1]+cur[1],p[k+2]+cur[0],p[k+3]+cur[1]
                if prev_ctrl and last_cmd in ('C','c','S','s'):
                    rc = [2*cur[0]-prev_ctrl[0], 2*cur[1]-prev_ctrl[1]]
                else:
                    rc = cur[:]
                out.append(f"C{rc[0]:g} {rc[1]:g} {x2:g} {y2:g} {x:g} {y:g}")
                cur=[x,y]; prev_ctrl=[x2,y2]
        elif c == 'Q':
            for k in range(0, len(p), 4):
                x1,y1,x,y = p[k:k+4]
                cur=[x,y]; prev_ctrl=[x1,y1]
                out.append(f"Q{x1:g} {y1:g} {x:g} {y:g}")
        elif c == 'q':
            for k in range(0, len(p), 4):
                x1,y1,x,y = p[k]+cur[0],p[k+1]+cur[1],p[k+2]+cur[0],p[k+3]+cur[1]
                cur=[x,y]; prev_ctrl=[x1,y1]
                out.append(f"Q{x1:g} {y1:g} {x:g} {y:g}")
        elif c == 'T':
            for k in range(0, len(p), 2):
                x,y = p[k:k+2]
                if prev_ctrl and last_cmd in ('Q','q','T','t'):
                    rc = [2*cur[0]-prev_ctrl[0], 2*cur[1]-prev_ctrl[1]]
                else:
                    rc = cur[:]
                out.append(f"Q{rc[0]:g} {rc[1]:g} {x:g} {y:g}")
                cur=[x,y]; prev_ctrl=[x,y]  # 平滑二次曲线控制点就是当前点反射
        elif c == 't':
            for k in range(0, len(p), 2):
                x,y = p[k]+cur[0], p[k+1]+cur[1]
                if prev_ctrl and last_cmd in ('Q','q','T','t'):
                    rc = [2*cur[0]-prev_ctrl[0], 2*cur[1]-prev_ctrl[1]]
                else:
                    rc = cur[:]
                out.append(f"Q{rc[0]:g} {rc[1]:g} {x:g} {y:g}")
                cur=[x,y]; prev_ctrl=[x,y]
        elif c == 'A':
            for k in range(0, len(p), 7):
                rx,ry,rot,la,sw,x,y = p[k:k+7]
                cur=[x,y]
                out.append(f"A{rx:g} {ry:g} {rot:g} {int(la)} {int(sw)} {x:g} {y:g}")
            prev_ctrl=None
        elif c == 'a':
            for k in range(0, len(p), 7):
                rx,ry,rot,la,sw,x,y = p[k:k+7]
                x += cur[0]; y += cur[1]; cur=[x,y]
                out.append(f"A{rx:g} {ry:g} {rot:g} {int(la)} {int(sw)} {x:g} {y:g}")
            prev_ctrl=None
        elif c in ('Z','z'):
            out.append("Z"); cur = start[:]
        last_cmd = c
    return " ".join(out)

def circle_to_path(cx, cy, r):
    return f"M{cx-r:g} {cy:g} A{r:g} {r:g} 0 1 1 {cx+r:g} {cy:g} A{r:g} {r:g} 0 1 1 {cx-r:g} {cy:g} Z"

def rect_to_path(x, y, w, h, rx=0):
    if not rx or rx <= 0:
        return f"M{x:g} {y:g} H{x+w:g} V{y+h:g} H{x:g} Z"
    r = min(rx, w/2, h/2)
    return (f"M{x+r:g} {y:g} H{x+w-r:g} A{r:g} {r:g} 0 0 1 {x+w:g} {y+r:g} "
            f"V{y+h-r:g} A{r:g} {r:g} 0 0 1 {x+w-r:g} {y+h:g} H{x+r:g} "
            f"A{r:g} {r:g} 0 0 1 {x:g} {y+h-r:g} V{y+r:g} A{r:g} {r:g} 0 0 1 {x+r:g} {y:g} Z")

def element_to_abs(el):
    """把一个 SVG 元素标记（circle/rect/path）转成绝对 path 'd' 片段。"""
    tag = el[0]
    attrs = dict(re.findall(r'(\w+)="([^"]*)"', el[1] or ""))
    if tag == 'circle':
        return circle_to_path(float(attrs['cx']), float(attrs['cy']), float(attrs['r']))
    if tag == 'rect':
        x=float(attrs['x']); y=float(attrs['y']); w=float(attrs['width']); h=float(attrs['height'])
        rx=float(attrs.get('rx', 0) or 0)
        return rect_to_path(x,y,w,h,rx)
    if tag == 'path':
        return path_abs(attrs['d'])
    raise ValueError(f"未知元素 {tag}")

def svg_to_abs(markup):
    """把一整个图标 SVG 标记（可含多个 circle/rect/path）转成单条绝对 path 'd'。"""
    elems = re.findall(r"<(circle|rect|path)([^>]*)/?>", markup)
    frags = [element_to_abs((e[0], e[1])) for e in elems]
    return " ".join(frags)

# ---------------------------------------------------------------------------
# 原型没有、需要补的文件类型 / 杂项图标（手绘干净绝对路径，24x24 描边线性）
# ---------------------------------------------------------------------------
FALLBACK = {
    # 任务列表左侧按扩展名显示的文件类型图标
    "cube":      "M12 2 L21 7 V17 L12 22 L3 17 V7 Z M12 2 L12 12 M21 7 L12 12 M3 7 L12 12 M12 12 L12 22",
    "archive":   "M3 4 H21 V8 H3 Z M5 8 V19 A1 1 0 0 0 6 20 H18 A1 1 0 0 0 19 19 V8 M10 12 H14",
    "package":   "M12 3 L21 8 V16 L12 21 L3 16 V8 Z M12 3 V21 M3 8 L21 16 M3 16 L21 8",
    "music":     "M9 18 V5 L20 3 V16 M9 18 A3 3 0 1 1 3 18 A3 3 0 1 1 9 18 Z M20 16 A3 3 0 1 1 14 16 A3 3 0 1 1 20 16 Z",
    "film":      "M4 4 H20 V20 H4 Z M8 4 V20 M16 4 V20 M4 9 H20 M4 15 H20",
    "image":     "M4 4 H20 V20 H4 Z M8.5 11 A2.5 2.5 0 1 0 13.5 11 A2.5 2.5 0 1 0 8.5 11 Z M20 15 L15 11 L6 18",
    # 状态/控件用
    "alert-circle": "M12 12a9 9 0 1 0 18 0a9 9 0 1 0 -18 0Z M12 7.5 V12.5 M12 15.5 a0.9 0.9 0 1 0 1.8 0 a0.9 0.9 0 1 0 -1.8 0 Z",
    "clock":     "M12 12a9 9 0 1 0 18 0a9 9 0 1 0 -18 0Z M12 7 V12 L15.5 14",
    "chevron-down": "M6 9 L12 15 L18 9",
    "chevron-up":   "M6 15 L12 9 L18 15",
    "filter":    "M3 5 H21 L14 13 V20 L10 22 V13 Z",
}

# 代码里动态切换会用到的 moon（原型已有）
# 被 XAML 直接引用的 Kind 集合在生成后做断言
REFERENCED = {
    "alert-circle","check-circle","check-square","chevron-down","clipboard","clock",
    "contrast","copy","download","external-link","file","file-text","folder",
    "folder-open","gauge","grid","heart","layers","link","list","magnet","pause",
    "play","plus","refresh","search","settings","sparkle","stop","sun","terminal",
    "x","zap",
    # BadgeIcon 返回值
    "cube","archive","package","music","film","image","magnet","file-text","file",
    # 代码动态设置
    "moon",
}

def resolve(name):
    if name in proto_icons:
        return svg_to_abs(proto_icons[name])
    if name in FALLBACK:
        return FALLBACK[name]
    raise KeyError(f"图标 {name} 既不在原型 GLYPH 也不在补充表")

# ---- 生成 ----
order = sorted(set(proto_icons) | set(FALLBACK))
lines = []
lines.append('<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"')
lines.append('                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"')
lines.append('                    xmlns:ctrl="clr-namespace:MDVTool.Controls">')
lines.append("")
lines.append('    <!-- ============================================================')
lines.append('         统一矢量图标库 · 24x24 viewBox / 线性描边 / 当前色')
lines.append('         来源：docs/reference/全新设计-可交互原型-v2.html 的 GLYPH（已由用户确认）')
lines.append('         不依赖 Segoe Fluent Icons 等系统字体，换机器不会缺字变方块')
lines.append('         每个 SVG 元素已独立绝对化后拼接，杜绝相对命令错位')
lines.append('         ============================================================ -->')
lines.append("")
lines.append('    <Style TargetType="{x:Type ctrl:VectorIcon}">')
lines.append('        <Setter Property="Width" Value="16"/>')
lines.append('        <Setter Property="Height" Value="16"/>')
lines.append('        <Setter Property="Focusable" Value="False"/>')
lines.append('        <Setter Property="IsHitTestVisible" Value="False"/>')
lines.append('        <Setter Property="Template">')
lines.append('            <Setter.Value>')
lines.append('                <ControlTemplate TargetType="{x:Type ctrl:VectorIcon}">')
lines.append('                    <Path Data="{TemplateBinding Shape}" Stretch="Uniform"')
lines.append('                          Stroke="{TemplateBinding Foreground}" StrokeThickness="1.7"')
lines.append('                          StrokeStartLineCap="Round" StrokeEndLineCap="Round"')
lines.append('                          StrokeLineJoin="Round" Fill="Transparent"')
lines.append('                          SnapsToDevicePixels="True"/>')
lines.append('                </ControlTemplate>')
lines.append('            </Setter.Value>')
lines.append('        </Setter>')
lines.append('    </Style>')
lines.append("")

missing = []
for name in sorted(REFERENCED):
    if name not in proto_icons and name not in FALLBACK:
        missing.append(name)
        continue
    d = resolve(name)
    lines.append(f'    <PathGeometry x:Key="Ico.{name}" Figures="{d}"/>')

lines.append("")
lines.append('</ResourceDictionary>')
open(OUT, "w", encoding="utf-8").write("\n".join(lines))

print(f"生成 {OUT}")
print(f"原型图标 {len(proto_icons)} 个，补充 {len(FALLBACK)} 个，输出键 {len(REFERENCED)-len(missing)} 个")
if missing:
    print("!! 缺失未解析:", missing)
    sys.exit(1)
print("OK - 所有被引用的 Kind 都有对应键")
