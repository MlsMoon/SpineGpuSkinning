"""Original vector artwork and Spine 4.2 authoring source. Requires Pillow.
Run from any directory: python generate_characters.py
Coordinates in artwork use SVG pixels; skeleton coordinates use Y up.
"""
from pathlib import Path
import json, math, copy
from PIL import Image, ImageDraw
ROOT = Path(__file__).resolve().parent.parent
S = 4
INK = '#253649'
PALETTE = {}
class Art:
    def __init__(self, w, h):
        self.w,self.h=w,h
        self.im=Image.new('RGBA',(w*S,h*S))
        self.d=ImageDraw.Draw(self.im)
        self.svg=[]
    def rect(self, box, fill, r=0, stroke=None, sw=2):
        fill=PALETTE.get(fill,fill);stroke=PALETTE.get(stroke,stroke)
        x,y,X,Y=box
        self.d.rounded_rectangle(tuple(v*S for v in box),r*S,fill,stroke,sw*S)
        self.svg.append(f'<rect x="{x}" y="{y}" width="{X-x}" height="{Y-y}" rx="{r}" fill="{fill}" stroke="{stroke or "none"}" stroke-width="{sw}"/>')
    def oval(self,box,fill,stroke=None,sw=2):
        fill=PALETTE.get(fill,fill);stroke=PALETTE.get(stroke,stroke)
        x,y,X,Y=box
        self.d.ellipse(tuple(v*S for v in box),fill,stroke,sw*S)
        self.svg.append(f'<ellipse cx="{(x+X)/2}" cy="{(y+Y)/2}" rx="{(X-x)/2}" ry="{(Y-y)/2}" fill="{fill}" stroke="{stroke or "none"}" stroke-width="{sw}"/>')
    def poly(self, points, fill):
        fill=PALETTE.get(fill,fill)
        self.d.polygon([(x*S,y*S) for x,y in points],fill)
        self.svg.append('<polygon points="'+' '.join(f'{x},{y}' for x,y in points)+f'" fill="{fill}"/>')
    def save(self,path):
        path.write_text(f'<svg xmlns="http://www.w3.org/2000/svg" width="{self.w}" height="{self.h}" viewBox="0 0 {self.w} {self.h}">'+''.join(self.svg)+'</svg>\n',encoding='utf-8')
        return self.im.resize((self.w,self.h),Image.Resampling.LANCZOS)

def artwork(robot):
    parts={}
    a=Art(96,96);parts['head']=a
    if robot:
        a.rect((9,14,86,80),'#91d4d6',23,INK,3)
        a.rect((13,18,82,48),'#c6f2e8',18)
        a.rect((3,40,15,63),'#557d91',5,INK)
        a.rect((80,40,92,63),'#557d91',5,INK)
        a.rect((18,32,82,67),INK,14)
        a.rect((51,41,58,53),'#9cffe8',3)
        a.rect((68,41,75,53),'#9cffe8',3)
        a.rect((54,59,71,61),'#56bbc4',1)
        a.oval((24,70,30,76),'#467685')
        a.oval((68,70,74,76),'#467685')
    else:
        a.oval((12,21,83,85),INK)
        a.oval((15,24,81,82),'#efb98f')
        a.oval((18,29,79,76),'#ffd6ab')
        a.oval((9,48,27,66),'#eaaa83',INK)
        a.oval((16,51,23,61),'#ffceb0')
        a.oval((55,44,62,54),INK)
        a.oval((72,44,78,54),INK)
        a.oval((56,44,58,47),'#ffffff')
        a.oval((64,55,80,64),'#efac87')
        a.rect((58,67,71,69),'#a36250',1)
        a.poly([(15,47),(10,34),(17,20),(26,9),(47,7),(62,12),(80,13),(86,28),(78,37),(65,29),(53,33),(44,26),(33,40),(25,40),(24,51)],INK)
        a.poly([(17,31),(29,14),(46,12),(57,17),(72,18),(64,22),(48,20),(36,28)],'#70514d')
        a.oval((32,76,48,84),'#edb28d')
    a=Art(64,72);parts['body']=a
    a.rect((8,4,56,68),'#70afb9' if robot else '#c46144',15,INK,3)
    a.rect((13,7,51,56),'#a0deda' if robot else '#ee9560',12)
    if robot:
        a.rect((21,19,48,45),INK,6)
        a.oval((28,25,42,39),'#f9d77c')
        a.rect((22,53,44,57),'#427789',2)
    else:
        a.poly([(31,8),(39,9),(46,60),(38,61)],'#f7d08b')
        a.rect((14,42,29,56),'#cb714c',3)
        a.rect((16,43,27,46),'#ffbb7b',1)
        a.rect((9,59,55,65),INK,2)
        a.rect((33,59,41,65),'#e9c878',1)
    a=Art(28,40);parts['upperarm']=a
    a.rect((4,2,24,37),'#8bc9ce' if robot else '#e38858',9,INK)
    a.rect((7,5,13,29),'#b8ebdf' if robot else '#ffb477',3)
    a=Art(26,38);parts['forearm']=a
    a.rect((4,2,22,35),'#628b9f' if robot else '#ffd2a4',8,INK)
    a.rect((7,6,12,28),'#a1d9db' if robot else '#ffe2bc',3)
    a=Art(26,28);parts['hand']=a
    a.rect((4,3,23,25),'#acdcd9' if robot else '#ffd2a4',8,INK)
    a=Art(32,44);parts['thigh']=a
    a.rect((4,2,28,42),'#647d96' if robot else '#42546c',10,INK)
    a.rect((8,5,15,34),'#8faabd' if robot else '#64748a',3)
    a=Art(28,42);parts['shin']=a
    a.rect((4,2,24,40),'#9acecb' if robot else '#42546c',8,INK)
    a.rect((8,5,13,31),'#c6eee0' if robot else '#64748a',2)
    if robot:a.oval((8,3,21,16),'#f9d77c',INK)
    a=Art(48,28);parts['foot']=a
    a.rect((3,3,29,22),'#608f9f' if robot else '#9b654d',7,INK)
    a.rect((12,9,46,24),'#9bcfcf' if robot else '#c58a62',7,INK)
    a.rect((4,21,45,25),'#d8ece5' if robot else '#e8ca96',2,INK,1)
    a=Art(36,52);parts['pack']=a
    a.rect((3,3,33,49),'#e1b968' if robot else '#709d93',9,INK)
    a.rect((7,8,23,36),'#ffe09a' if robot else '#9fc9b0',6)
    a.rect((9,39,26,43),INK,2)
    a=Art(24,34);parts['accent']=a
    if robot:
        a.rect((10,10,14,32),INK,2)
        a.oval((4,2,21,19),'#ffd575',INK)
        a.oval((7,4,13,10),'#fff0b2')
    else:
        a.poly([(3,1),(21,5),(18,31),(11,26),(4,31)],INK)
        a.poly([(6,4),(18,7),(16,26),(11,22),(7,26)],'#7cb9af')
    return parts

def character(name,robot):
    out=ROOT/'Character'/name;out.mkdir(parents=True,exist_ok=True)
    source=ROOT/'Source~'/name;source.mkdir(exist_ok=True)
    palettes = [({}, 'default'),
        ({'#ee9560':'#79b99c','#c46144':'#437b74','#e38858':'#66a48e','#ffb477':'#a8d5b6','#cb714c':'#4a8c7c','#ffbb7b':'#abdcae','#709d93':'#b99870','#9fc9b0':'#e4c599'}, 'forest'),
        ({'#ee9560':'#8399ce','#c46144':'#50639b','#e38858':'#6d84ba','#ffb477':'#acbdea','#cb714c':'#576b9c','#ffbb7b':'#b7c5ec','#709d93':'#a07590','#9fc9b0':'#d2a5b3'}, 'midnight')]
    if robot:
        palettes=[({},'default'),
            ({'#91d4d6':'#e8ae7b','#c6f2e8':'#ffe0b3','#a0deda':'#edbc87','#70afb9':'#b98060','#8bc9ce':'#dfae7f','#b8ebdf':'#ffe4ba','#9acecb':'#e2aa79','#c6eee0':'#ffdbad','#acdcd9':'#edc699','#9bcfcf':'#deb186'},'copper'),
            ({'#91d4d6':'#d4a8d4','#c6f2e8':'#f5d6ea','#a0deda':'#dfbbdf','#70afb9':'#9c79a7','#8bc9ce':'#c9a1d2','#b8ebdf':'#f4d6e9','#9acecb':'#c7a2d4','#c6eee0':'#ecd2ed','#acdcd9':'#e3bde0','#9bcfcf':'#d0aedc'},'rose')]
    global PALETTE
    atlas=Image.new('RGBA',(512,512));x=y=4;row=0
    lines=[name+'.png','size: 512,512','format: RGBA8888','filter: Linear,Linear','repeat: none']
    for palette,skin in palettes:
        PALETTE=palette;parts=artwork(robot)
        skin_source=source/skin;skin_source.mkdir(exist_ok=True)
        for key,art in parts.items():
            if x+art.w+4>512:x=4;y+=row+8;row=0
            im=art.save(skin_source/(key+'.svg'));atlas.paste(im,(x,y))
            lines += [skin+'/'+key,'  rotate: false',f'  xy: {x}, {y}',f'  size: {art.w}, {art.h}',f'  orig: {art.w}, {art.h}','  offset: 0, 0','  index: -1']
            x+=art.w+8;row=max(row,art.h)
    assert y+row<=512
    PALETTE={};parts=artwork(robot)
    atlas.save(out/(name+'.png'))
    (out/(name+'.atlas.txt')).write_text('\n'.join(lines)+'\n',encoding='utf-8')
    bones=[];world={};slots=[];attachments={}
    def bone(n,parent=None,x=0,y=0):
        b={'name':n}
        if parent:b.update(parent=parent,x=x,y=y)
        bones.append(b);px,py=world.get(parent,(0,0));world[n]=(px+x,py+y)
    bone('root');bone('hip','root',0,55);bone('spine','hip',0,17);bone('chest','spine',0,20)
    bone('neck','chest',0,13);bone('head','neck',0,19)
    bone('pack','chest',-22,-5);bone('accent','head' if robot else 'neck',0 if robot else -19,39 if robot else -8)
    for side,offset in [('back',-8),('front',7)]:
        bone('thigh_'+side,'hip',offset,0);bone('shin_'+side,'thigh_'+side,0,-25)
        bone('foot_'+side,'shin_'+side,0,-24)
        bone('arm_'+side,'chest',offset,0);bone('forearm_'+side,'arm_'+side,0,-23)
        bone('hand_'+side,'forearm_'+side,0,-21)
    def region(slot,b,part,x=0,y=0,w=None,h=None,color=None):
        slots.append(dict(name=slot,bone=b,attachment=slot))
        if color:slots[-1]['color']=color
        art=parts[part]
        attachments[slot]={slot:dict(path=part,x=x,y=y,width=w or art.w,height=h or art.h)}
    def limb(side):
        shade='b0becaff' if side=='back' else None
        region('thigh_'+side,'thigh_'+side,'thigh',y=-12,w=23,h=32,color=shade)
        region('shin_'+side,'shin_'+side,'shin',y=-12,w=20,h=31,color=shade)
        region('foot_'+side,'foot_'+side,'foot',x=8,y=-1,w=34,h=20,color=shade)
    def arm(side):
        shade='b0becaff' if side=='back' else None
        region('arm_'+side,'arm_'+side,'upperarm',y=-12,w=20,h=29,color=shade)
        region('forearm_'+side,'forearm_'+side,'forearm',y=-11,w=18,h=27,color=shade)
        region('hand_'+side,'hand_'+side,'hand',y=-3,w=18,h=19,color=shade)
    arm('back');limb('back');region('pack','pack','pack',w=30,h=44);limb('front')
    slots.append(dict(name='body',bone='hip',attachment='body'))
    uvs=[];vertices=[];triangles=[];cols=9;rows=11
    for iy in range(rows):
        v=iy/(rows-1);wy=108-v*59
        weight=max(0,min(1,(wy-62)/32))
        for ix in range(cols):
            u=ix/(cols-1);wx=(u-.5)*49;uvs += [u,v];vertices.append(2)
            for bn,w in [('hip',1-weight),('chest',weight)]:
                bx,by=world[bn];vertices += [next(i for i,b in enumerate(bones) if b['name']==bn),round(wx-bx,4),round(wy-by,4),round(w,6)]
            if ix<cols-1 and iy<rows-1:
                k=iy*cols+ix;triangles += [k,k+cols,k+1,k+1,k+cols,k+cols+1]
    attachments['body']={'body':dict(type='mesh',path='body',uvs=uvs,triangles=triangles,vertices=vertices,hull=0,width=49,height=59)}
    region('head','head','head',w=76,h=76);region('accent','accent','accent',w=18,h=26);arm('front')
    def rotations(values):return [dict(time=round(i/(len(values)-1),4),value=a) for i,a in enumerate(values)]
    walk={}
    # Contact travels backwards relative to the torso; the return foot lifts behind the knee.
    for side,phase in [('front',0),('back',.5)]:
        channels={bn:[] for bn in ['thigh','shin','foot','arm','forearm']}
        for frame in range(33):
            t=frame/32;p=(t+phase)%1
            bounce=.7*(1-math.cos(4*math.pi*t))/2
            if p<.6:
                x=15.6-31.2*p/.6;lift=0;toe=0
            else:
                q=(p-.6)/.4;x=-15.6+31.2*q;lift=11*math.sin(math.pi*q);toe=10*math.sin(math.pi*q)
            y=9+lift-55-bounce
            knee=-math.acos(max(-1,min(1,(x*x+y*y-25*25-24*24)/(2*25*24))))
            thigh=math.atan2(x,-y)-math.atan2(24*math.sin(knee),25+24*math.cos(knee))
            values=[math.degrees(thigh),math.degrees(knee),-math.degrees(thigh+knee)+toe,
                    -20*math.cos(2*math.pi*p),-8-6*math.sin(2*math.pi*p)]
            for bn,value in zip(channels,values):channels[bn].append(dict(time=t,value=round(value,4)))
        for bn,keys in channels.items():walk[bn+'_'+side]={'rotate':keys}
    walk['hip']={'translate':[dict(time=i/32,x=0,y=round(.7*(1-math.cos(4*math.pi*i/32))/2,4)) for i in range(33)]}
    walk['chest']={'rotate':rotations([2,0,-2,0,2])}
    walk['head']={'rotate':rotations([-2,0,2,0,-2])}
    walk['accent']={'rotate':rotations([-9,6,9,-6,-9])}
    idle={'chest':{'rotate':[dict(time=0,value=-1),dict(time=.8,value=1),dict(time=1.6,value=-1)]}}
    skins=[]
    for _,skin in palettes:
        variant=copy.deepcopy(attachments)
        for slot,mapping in variant.items():
            for key,attachment in mapping.items():
                attachment['name']=skin+'/'+slot
                attachment['path']=skin+'/'+attachment['path']
        skins.append(dict(name=skin,attachments=variant))
    data=dict(skeleton=dict(hash=name+'-original-v2',spine='4.2.43',x=-50,y=-10,width=100,height=180,images='./'),bones=bones,slots=slots,skins=skins,animations=dict(idle=dict(bones=idle),walk=dict(bones=walk)))
    (out/(name+'.json')).write_text(json.dumps(data,ensure_ascii=False,separators=(',',':'))+'\n',encoding='utf-8')
    print(name, len(bones),'bones',len(uvs)//2,'weighted vertices',len(slots),'slots')

if __name__=='__main__':
    character('SampleDude',False)
    character('SampleRobot',True)
