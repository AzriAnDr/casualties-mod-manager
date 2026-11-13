"""Create the vector-based app icon; requires Pillow."""
from pathlib import Path
from PIL import Image, ImageDraw

assets=Path(__file__).resolve().parent.parent/'assets'
assets.mkdir(exist_ok=True)
svg='''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256">
<rect x="8" y="8" width="240" height="240" rx="54" fill="#10272c"/>
<rect x="42" y="49" width="146" height="46" rx="12" fill="#2b5c63"/>
<rect x="42" y="105" width="146" height="46" rx="12" fill="#419389"/>
<rect x="42" y="161" width="146" height="46" rx="12" fill="#74dfc3"/>
<path d="M61 72h28m-28 56h48m-48 56h68" stroke="#e6fff7" stroke-width="8" stroke-linecap="round"/>
<circle cx="190" cy="75" r="35" fill="#f1bd65"/>
<path d="M175 75h30m-15-15v30" stroke="#10272c" stroke-width="9" stroke-linecap="round"/>
</svg>'''
(assets/'AppIcon.svg').write_text(svg,encoding='utf8')
scale=4
im=Image.new('RGBA',(256*scale,256*scale))
d=ImageDraw.Draw(im)
def box(coords,color,radius):d.rounded_rectangle(tuple(int(v*scale) for v in coords),radius*scale,fill=color)
def line(coords,color,width):
    pts=[(int(x*scale),int(y*scale)) for x,y in coords]
    d.line(pts,fill=color,width=width*scale)
    r=width*scale/2
    for x,y in (pts[0],pts[-1]):d.ellipse((x-r,y-r,x+r,y+r),fill=color)
box((8,8,248,248),'#10272c',54)
for y,color in ((49,'#2b5c63'),(105,'#419389'),(161,'#74dfc3')):box((42,y,188,y+46),color,12)
for y,end in ((72,89),(128,109),(184,129)):line([(61,y),(end,y)],'#e6fff7',8)
d.ellipse((155*scale,40*scale,225*scale,110*scale),fill='#f1bd65')
line([(175,75),(205,75)],'#10272c',9)
line([(190,60),(190,90)],'#10272c',9)
im=im.resize((256,256),Image.Resampling.LANCZOS)
im.save(assets/'AppIcon-256.png')
im.save(assets/'AppIcon.ico',format='ICO',sizes=[(n,n) for n in (16,20,24,32,40,48,64,128,256)])
print(assets/'AppIcon.ico')
