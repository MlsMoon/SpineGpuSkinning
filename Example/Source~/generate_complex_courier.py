"""Original complex courier: cloth meshes, weighted chains, deform, clipping and draw order.
Run after generate_characters.py. No project character artwork is copied.
"""
from pathlib import Path
import json,copy,math
from PIL import Image
from generate_characters import ROOT,Art,INK

def generate(ultra=False):
    character_name = "UltraCourier" if ultra else "ComplexCourier"
    src=ROOT/'Character'/'SampleDude';out=ROOT/'Character'/character_name;out.mkdir(exist_ok=True)
    source=ROOT/'Source~'/character_name;source.mkdir(exist_ok=True)
    data=json.loads((src/'SampleDude.json').read_text(encoding='utf-8'))
    data['skeleton']['hash']=character_name+'-original-v1'
    atlas=Image.new('RGBA',(1024,1024));atlas.paste(Image.open(src/'SampleDude.png'),(0,0))
    lines=(src/'SampleDude.atlas.txt').read_text(encoding='utf-8').replace('SampleDude.png',character_name+'.png').replace('size: 512,512','size: 1024,1024').splitlines()
    x,y,row=4,520,0
    for skin,color,light in [('default','#b5584d','#f2b97f'),('forest','#45897f','#add8aa'),('midnight','#686ea7','#cab4e6')]:
        parts={}
        a=Art(128,160);parts['cape']=a
        a.poly([(61,3),(88,10),(117,131),(90,152),(69,142),(45,155),(10,134),(34,50)],INK)
        a.poly([(62,7),(85,14),(111,129),(88,145),(68,136),(44,148),(17,131),(39,52)],color)
        a.poly([(64,12),(73,14),(71,131),(63,139),(47,143)],light)
        a.poly([(39,64),(45,55),(35,125),(23,130)],light)
        for i in range(4):a.oval((50+i*11,115,56+i*11,121),'#e7bf76')
        a=Art(144,48);parts['scarf']=a
        a.poly([(3,16),(18,3),(62,13),(100,7),(138,17),(127,27),(136,40),(93,33),(59,38),(14,31)],INK)
        a.poly([(8,17),(19,8),(62,18),(100,12),(131,18),(121,26),(129,33),(93,28),(59,33),(17,27)],light)
        a.poly([(18,23),(61,27),(92,23),(124,26),(129,33),(93,28),(59,33),(17,27)],color)
        a=Art(96,32);parts['strap']=a
        a.rect((2,8,94,24),INK,9);a.rect((5,11,91,21),'#bd9c6e',7)
        for i in range(6):a.rect((10+i*13,12,15+i*13,20),'#ecd5a2',2)
        a=Art(64,52);parts['hair']=a
        a.poly([(3,6),(38,3),(59,21),(45,25),(56,36),(38,33),(44,48),(18,39),(5,22)],INK)
        a.poly([(8,10),(34,8),(48,19),(30,17),(44,28),(25,23),(30,37),(17,30)],'#70514d')
        a=Art(96,48);parts['visor']=a
        a.rect((3,7,91,42),INK,14);a.rect((8,11,87,37),'#264c66',12)
        a.rect((15,15,81,21),'#437487',3)
        a=Art(96,48);parts['shine']=a
        a.poly([(9,46),(34,2),(60,2),(35,46)],'#6ccdd5')
        a.poly([(47,46),(72,2),(83,2),(58,46)],'#b4f0df')
        if ultra:
            for tail in ['ribbon_left','ribbon_right']:
                a=Art(40,144);parts[tail]=a
                a.poly([(10,2),(31,3),(26,125),(18,140),(8,124)],INK)
                a.poly([(13,6),(27,6),(22,124),(18,134),(12,123)],color)
                a.poly([(15,7),(19,7),(17,122),(15,119)],light)
                for i in range(5):a.rect((15,30+i*17,23,33+i*17),'#e6c888',1)
        for mark in ['badge','badge_alt']:
            a=Art(24,24);parts[mark]=a
            a.oval((2,2,22,22),INK);a.oval((5,5,19,19),light if mark=='badge' else '#86e4dd')
            a.poly([(12,6),(14,10),(18,12),(14,14),(12,18),(10,14),(6,12),(10,10)],'#fff0ba')
        folder=source/skin;folder.mkdir(exist_ok=True)
        for part,art in parts.items():
            if x+art.w+4>1024:x=4;y+=row+8;row=0
            atlas.paste(art.save(folder/(part+'.svg')),(x,y))
            lines += [skin+'/'+part,'  rotate: false',f'  xy: {x}, {y}',f'  size: {art.w}, {art.h}',f'  orig: {art.w}, {art.h}','  offset: 0, 0','  index: -1']
            x+=art.w+8;row=max(row,art.h)
    assert y+row<=1024
    atlas.save(out/(character_name+'.png'));(out/(character_name+'.atlas.txt')).write_text('\n'.join(lines)+'\n',encoding='utf-8')
    bones=data['bones'];world={}
    for b in bones:
        px,py=world.get(b.get('parent'),(0,0));world[b['name']]=(px+b.get('x',0),py+b.get('y',0))
    def bone(n,parent,x,y):
        px,py=world[parent];bones.append(dict(name=n,parent=parent,x=x-px,y=y-py));world[n]=(x,y)
    grids={}
    specifications=[('cape',2,12 if ultra else 5,(-45,99,-15,29),'chest'),
        ('scarf',2,6 if ultra else 4,(-9,108,-67,93),'neck'),
        ('strap',2,4 if ultra else 3,(-21,89,8,58),'chest'),
        ('hair',2,4 if ultra else 2,(-15,151,-32,131),'head')]
    if ultra:
        specifications += [('ribbon_left',2,7,(-26,69,-40,8),'hip'),
                           ('ribbon_right',2,7,(-10,67,3,9),'hip')]
    for name,cols,rows,box,parent in specifications:
        left,top,right,bottom=box;grid=[]
        for iy in range(rows):
            rowbones=[]
            for ix in range(cols):
                n=f'{name}_{iy}_{ix}';bone(n,parent,left+(right-left)*ix/(cols-1),top+(bottom-top)*iy/(rows-1));rowbones.append(n)
            grid.append(rowbones)
        grids[name]=(grid,box)
    def mesh(name,cols,rows,skin):
        grid,box=grids[name];left,top,right,bottom=box
        verts=[];uv=[];tri=[]
        for iy in range(rows):
            v=iy/(rows-1);gy=v*(len(grid)-1);r=min(len(grid)-2,int(gy));fy=gy-r
            for ix in range(cols):
                u=ix/(cols-1);x=left+(right-left)*u;y=top+(bottom-top)*v
                uv += [u,v];verts.append(4)
                for bn,w in [(grid[r][0],(1-u)*(1-fy)),(grid[r][1],u*(1-fy)),(grid[r+1][0],(1-u)*fy),(grid[r+1][1],u*fy)]:
                    bx,by=world[bn];verts += [next(i for i,b in enumerate(bones) if b['name']==bn),round(x-bx,5),round(y-by,5),round(w,7)]
                if ix<cols-1 and iy<rows-1:
                    k=iy*cols+ix;tri += [k,k+cols,k+1,k+1,k+cols,k+cols+1]
        return dict(name=skin+'/'+name,path=skin+'/'+name,type='mesh',uvs=uv,vertices=verts,triangles=tri,hull=0,width=abs(right-left),height=abs(top-bottom))
    slots=data['slots']
    def insert(n,b,at):slots.insert(at,dict(name=n,bone=b,attachment=n))
    insert('cape','chest',0)
    insert('strap','chest',next(i for i,s in enumerate(slots) if s['name']=='head'))
    insert('scarf','neck',next(i for i,s in enumerate(slots) if s['name']=='head'))
    insert('hair','head',next(i for i,s in enumerate(slots) if s['name']=='head'))
    face=next(i for i,s in enumerate(slots) if s['name']=='head')+1
    for i,n in enumerate(['visor','visor_clip','shine']):insert(n,'head',face+i)
    insert('badge','chest',len(slots))
    dims={'cape':(21,25),'scarf':(13,21),'strap':(9,13),'hair':(9,9)}
    if ultra:
        insert('ribbon_left','hip',1);insert('ribbon_right','hip',2)
        dims={'cape':(21,49),'scarf':(13,31),'strap':(9,19),'hair':(9,17),
              'ribbon_left':(9,31),'ribbon_right':(9,31)}
    for skin in data['skins']:
        n=skin['name'];a=skin['attachments']
        for part,(c,r) in dims.items():a[part]={part:mesh(part,c,r,n)}
        for part,w,h,x,y in [('visor',50,25,8,-4),('shine',72,28,8,-4),('badge',13,13,13,-2)]:
            a[part]={part:dict(name=n+'/'+part,path=n+'/'+part,width=w,height=h,x=x,y=y)}
        a['badge']['badge_alt']=dict(a['badge']['badge'],name=n+'/badge_alt',path=n+'/badge_alt')
        clip=[]
        for i in range(24):
            angle=2*math.pi*i/24;radius=.73 if i in (5,6,7) else 1
            clip += [round(8+22*math.cos(angle)*radius,4),round(-4+7*math.sin(angle)*radius,4)]
        a['visor_clip']={'visor_clip':dict(type='clipping',end='shine',vertexCount=24,vertices=clip)}
    walk=data['animations']['walk'];idle=data['animations']['idle']
    for anim,duration in [(walk,1),(idle,1.6)]:
        anim['attachments']={}
        for name,(grid,box) in grids.items():
            for row,items in enumerate(grid):
                for col,bn in enumerate(items):
                    anim['bones'][bn]={'translate':[dict(time=duration*i/8,x=round((row+.3)*.45*math.sin(2*math.pi*i/8-row*.45+col*.6),4),y=round(.35*row*math.sin(2*math.pi*i/8+row*.6),4)) for i in range(9)]}
        for skin in data['skins']:
            n=skin['name'];anim['attachments'][n]={}
            for part,(cols,rows) in dims.items():
                frames=[]
                for frame in range(5):
                    offsets=[]
                    for iy in range(rows):
                        v=iy/(rows-1)
                        for ix in range(cols):
                            u=ix/(cols-1);dx=1.1*v*math.sin(frame*math.pi/2+v*6+u*4);dy=.5*v*math.sin(frame*math.pi/2+u*5)
                            offsets.extend([round(dx,4),round(dy,4)]*4)
                    frames.append(dict(time=duration*frame/4,vertices=offsets))
                anim['attachments'][n][part]={part:{'deform':frames}}
        anim['slots']={'badge':{'attachment':[dict(time=0,name='badge'),dict(time=duration*.48,name='badge_alt'),dict(time=duration*.72,name='badge')]},
                       'shine':{'rgba':[dict(time=0,color='80e9ef55'),dict(time=duration*.5,color='aaffff99'),dict(time=duration,color='80e9ef55')]}}
        anim['drawOrder']=[dict(time=0),dict(time=duration*.5,offsets=[dict(slot='scarf',offset=2)]),dict(time=duration*.75)]
    (out/(character_name+'.json')).write_text(json.dumps(data,separators=(',',':'))+'\n',encoding='utf-8')
    counts={}
    for skin in data['skins']:
        counts[skin['name']]=sum(len(a.get('uvs',[]))//2 if a.get('type')=='mesh' else 4 if a.get('type','region')=='region' else 0 for m in skin['attachments'].values() for a in m.values())
    print(character_name+':',len(bones),'bones',len(slots),'slots','vertices including alternate attachments:',counts)
if __name__=='__main__':
    generate()
    generate(True)
