# Vector TrackVice icon on a 1024 unit canvas, in the same style and colours as the LogVice, PolicyVice and PxeVice icons:
# the three lines on the left, and a progress ring with a check where LogVice has its magnifier.
NAVY, CYAN, PINK = '#011133', '#00EEFE', '#FF199A'

def at_least(size, base, table):
    px = table.get(size, 0)
    return max(base, px * 1024.0 / size)

def app(size):
    w = at_least(size, 40, {16: 1.3, 20: 1.4, 24: 1.5, 32: 1.6, 40: 1.8, 48: 1.9, 64: 2.3})
    lines = [(155, 420, 358), (155, 390, 483), (155, 340, 608)] if size > 16 else [(155, 400, 400), (155, 340, 590)]
    parts = ['<rect x="20" y="20" width="984" height="984" rx="150" fill="%s"/>' % NAVY]
    for x0, x1, y in lines:
        parts.append('<line x1="%g" y1="%g" x2="%g" y2="%g" stroke="%s" stroke-width="%g" stroke-linecap="round"/>' % (x0 + w / 2, y, x1, y, CYAN, w))
    # Progress ring: centre 662,492, radius 172, three quarters from the top clockwise, with a dot where progress has reached.
    parts.append('<path d="M 662 320 A 172 172 0 1 1 490 492" fill="none" stroke="%s" stroke-width="%g" stroke-linecap="round"/>' % (PINK, w))
    parts.append('<circle cx="490" cy="492" r="%g" fill="%s"/>' % (w * 0.95, PINK))
    check = 'M 596 494 L 646 544 L 736 450' if size > 20 else 'M 588 494 L 644 550 L 744 446'
    parts.append('<path d="%s" fill="none" stroke="%s" stroke-width="%g" stroke-linecap="round" stroke-linejoin="round"/>' % (check, CYAN, w))
    return '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1024 1024">' + ''.join(parts) + '</svg>'
