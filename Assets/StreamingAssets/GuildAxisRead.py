# -*- coding: utf-8 -*-
"""Local Windows guild-chart reader. GUILD_AXIS_IMAGE or bitmap clipboard -> UTF-8 stdout.

Requires rapidocr-onnxruntime==1.4.4 with its bundled PP-OCRv4 models. A portable
installation can point GUILD_AXIS_OCR_LIB at its local Python package directory.
No screenshots, recognition results, or downloaded models are written by this helper.
"""
import json
from collections import Counter
from functools import lru_cache
import os
import re
import sys
import unicodedata
from importlib.metadata import version
from pathlib import Path
from typing import NamedTuple

sys.dont_write_bytecode = True
for _key in ('OMP_NUM_THREADS', 'OPENBLAS_NUM_THREADS', 'MKL_NUM_THREADS', 'NUMEXPR_NUM_THREADS'):
    os.environ[_key] = '2'
if os.environ.get('GUILD_AXIS_OCR_LIB'):
    sys.path.insert(0, os.environ['GUILD_AXIS_OCR_LIB'])

CHECK = '【请核对】'
CLOCK = re.compile(r'(?:[01]:[0-5]\d|\d{1,3})\Z')
GROWTH = ('等级', 'rank', '星级', '专一/专二', '专1/专2')


def norm(s):
    return re.sub(r'\s+', '', unicodedata.normalize('NFKC', s)).replace('–', '-').replace('—', '-').replace('【','[').replace('】',']').replace('丨','|')


class Word(NamedTuple):
    left: float
    top: float
    right: float
    bottom: float
    s: str
    p: float

    @property
    def y(self):
        return (self.top + self.bottom) / 2


class Reader:
    def __init__(self):
        import cv2
        import numpy as np
        from rapidocr_onnxruntime import RapidOCR
        if version('rapidocr-onnxruntime') != '1.4.4':
            raise ValueError('需要本地 rapidocr-onnxruntime==1.4.4 及其随包中文模型。')
        self.cv, self.np = cv2, np
        self.names={}
        data=Path(__file__).resolve().parent/'Datas'
        for name in ('chara_name.json','nickname.json','UnitNickNameDic.json'):
            f=data/name
            if not f.is_file(): continue
            for unit, aliases in json.loads(f.read_bytes()).items():
                key=str(unit)[:4]
                for alias in aliases if isinstance(aliases,list) else [aliases]:
                    a=norm(alias).lower()
                    if a: self.names[a]=key if self.names.get(a,key)==key else ''
        cv2.setNumThreads(1)
        self.ocr = RapidOCR(intra_op_num_threads=2, inter_op_num_threads=1, use_cls=False,
                            print_verbose=False, text_score=.38, det_box_thresh=.42)

    def words(self, img):
        cv, np = self.cv, self.np
        h, w = img.shape[:2]
        scale = min(2, 1120/w, 1700/h)
        view = cv.resize(img, None, fx=scale, fy=scale, interpolation=cv.INTER_CUBIC if scale > 1 else cv.INTER_AREA)
        rows, _ = self.ocr(view, use_cls=False)
        out = []
        for box, s, p in rows or []:
            pts = np.asarray(box)/scale
            (x, y), (xx, yy) = pts.min(axis=0), pts.max(axis=0)
            if s.strip():
                out.append(Word(x, y, xx, yy, norm(s), float(p)))
        return out

    def lines(self, words):
        lines = []
        for box in sorted(words, key=lambda b: b.y):
            if not lines or abs(self.np.median([w.y for w in lines[-1]])-box.y) > .57*(box.bottom-box.top):
                lines.append([])
            lines[-1].append(box)
        return [sorted(line, key=lambda b: b.left) for line in lines]

    def divider(self, img, head, a, b):
        np = self.np
        left, right = round(a), round(b)
        if right-left < 3:
            return (left+right)//2
        top = max(0, round(min(x.top for x in head)))
        bot = min(img.shape[0], round(max(x.bottom for x in head))+1)
        v = img[top:bot,left:right].astype('int16')
        d = np.quantile(np.abs(np.diff(v,axis=1)).max(2),.28,axis=0)
        i = int(d.argmax())
        return left+i+1 if d[i] > 9 else (left+right)//2

    def cuts(self, img, sec, head, words):
        np = self.np
        h, w = img.shape[:2]
        boxh = np.median([b.bottom-b.top for b in head]); center = np.median([b.y for b in head]); gap = max(3,round(boxh*.27))
        strip = img[:,max(1,round(sec*.1)):max(3,round(sec*.89))].astype('int16')
        delta = np.abs(np.diff(strip,axis=0)).max(axis=2)
        score = np.quantile(delta,.32,axis=1)
        # Also require a horizontal discontinuity on the rest of the page: isolated glyphs are not grid lines.
        wide = img[:,::4].astype('int16'); other = np.quantile(np.abs(np.diff(wide,axis=0)).max(axis=2),.5,axis=1)
        ids = np.flatnonzero((score>5) & ((other>3) | (score>24))) + 1
        groups = np.split(ids, np.flatnonzero(np.diff(ids)>gap)+1)
        borders = [int(round(np.average(g,weights=score[g-1]+.1))) for g in groups if len(g)]
        borders = [y for y in borders if y>center+.20*boxh]
        if not borders or borders[0] > center+boxh*2:
            raise ValueError('无法确定时间表的真实行边界，请裁剪或粘贴文字。')
        if h-borders[-1] > boxh*.55:
            borders.append(h)
        anchors = [b for b in words if (b.left+b.right)/2<sec and b.y>center and (CLOCK.fullmatch(b.s) or b.s in ('开局','开场'))]
        # A false JPEG edge through the middle of a detected clock cannot separate two rows.
        borders = [y for y in borders if not any(b.top+.18*(b.bottom-b.top)<y<b.bottom-.18*(b.bottom-b.top) for b in anchors)]
        # A pair of separately detected clocks proves that there must be a separator,
        # even where JPEG smoothing has almost erased the printed grid line.
        centers = [center] + sorted(b.y for b in anchors)
        for a,b in zip(centers,centers[1:]):
            if b-a > .75*boxh and not any(a<y<b for y in borders):
                mid=(a+b)/2; rad=(b-a)*.22
                lo=max(0,int(mid-rad)); hi=min(h-1,int(mid+rad)+1)
                borders.append(lo+1+int(score[lo:hi].argmax()) if hi>lo else round(mid))
        borders=sorted(set(borders))
        return [(y,yy) for y,yy in zip(borders,borders[1:]) if yy-y>max(6,.54*boxh)]

    def snippets(self, img, x0, x1, y0, y1):
        """Extract ink, not the cell fill or the grid. Split wrapped text and wide inter-cell gaps."""
        np, cv = self.np, self.cv
        patch = img[y0:y1,x0:x1]
        if min(patch.shape[:2]) < 3:
            return []
        ph,pw = patch.shape[:2]
        band = max(1,min(3,ph//10))
        bg = np.median(np.concatenate([patch[:band],patch[-band:]]),axis=0)
        dist = np.max(np.abs(patch.astype('float32')-bg.astype('float32')),axis=2)
        mask = dist > 39
        mask[:,mask.mean(0)>.91] = False
        mask[mask.mean(1)>.84] = False
        active = np.flatnonzero(mask.sum(1)>=1)
        snippets = []
        for ys in np.split(active,np.flatnonzero(np.diff(active)>max(2,ph*.09))+1):
            if len(ys)<3: continue
            a,b = int(ys[0]),int(ys[-1]+1); ht=b-a
            xs = np.flatnonzero(mask[a:b].any(0))
            for cols in np.split(xs,np.flatnonzero(np.diff(xs)>max(6,ht*1.05))+1):
                if not len(cols): continue
                l,r=int(cols[0]),int(cols[-1]+1)
                ink=mask[a:b,l:r]
                if ink.sum()<max(5,ht*.65) or r-l<2: continue
                box = dist[a:b,l:r].copy(); box[~ink & (box>100)] = 0
                contrast = max(65,np.percentile(box[ink],89))
                bw=(255-np.clip(box*247/contrast,0,255)).astype('uint8')
                pad=max(2,round(ht*.17)); out=cv.copyMakeBorder(bw,pad,pad,pad,pad,cv.BORDER_CONSTANT,value=255)
                snippets.append((x0+l,y0+a,x0+r,y0+b,cv.cvtColor(out,cv.COLOR_GRAY2BGR)))
        return snippets

    @staticmethod
    def notation(s):
        s=norm(s).lower().replace('丨','|').replace('【','[').replace('】',']')
        return re.sub(r'(?<![o0x×〇○])([o0x×〇○]{5})(?![o0x×〇○])',lambda m:''.join('x' if c in 'x×' else 'o' for c in m[1]),s)

    def actor_tokens(self, s):
        s=self.notation(s).replace('auto',''); s=re.sub(r'[()\d:.]','',s)
        if re.search('(?:开|关|目押|目压|普攻|容错|后|抬手|boss|^[ox]+$)',s): return []
        return [t for t in re.split(r'[-|、,;()]',s) if t and re.fullmatch(r'[a-z\u3400-\u9fff]+',t)]

    def reruns(self,img,box):
        cv,np=self.cv,self.np
        l,t,r,b=box; h,w=img.shape[:2]; ht=b-t; p=max(1,round(ht*.14));
        src=img[max(0,int(t)-p):min(h,int(b)+p+1),max(0,int(l)-p):min(w,int(r)+p+1)]
        if not src.size: return []
        native=cv.resize(src,None,fx=2,fy=2,interpolation=cv.INTER_CUBIC)
        gray=cv.cvtColor(native,cv.COLOR_BGR2GRAY)
        if np.median(gray)<115: gray=255-gray
        smooth=cv.GaussianBlur(gray,(0,0),1.5); sharp=cv.addWeighted(gray,1.8,smooth,-.8,0)
        otsu=cv.threshold(sharp,0,255,cv.THRESH_BINARY+cv.THRESH_OTSU)[1]
        pad=max(2,p); mono=cv.cvtColor(cv.copyMakeBorder(otsu,pad,pad,pad,pad,cv.BORDER_CONSTANT,value=255),cv.COLOR_GRAY2BGR)
        return [native,mono]

    def title(self, text, img, line, reads):
        prefix=re.match(r'^[a-e][1-5]',text,re.I)
        if not prefix: return [CHECK+'未识别完整会战标题']
        def team(s):
            s=s.lower(); s=re.sub(r'^[a-e][1-5]','',s)
            s=re.split(r'-?\d+(?:\.\d+)?(?:w|万|亿)',s,maxsplit=1)[0].strip('-:、')
            @lru_cache(None)
            def scan(t, ids):
                if len(ids)==5:return () if not t else None
                for a,id_ in self.names.items():
                    if id_ and id_ not in ids and t.startswith(a):
                        rest=scan(t[len(a):].lstrip('-、/'),ids+(id_,))
                        if rest is not None: return (a,)+rest
                return None
            return scan(s,())
        if team(text):return [text]
        # Read the actual title crop again. Do not map similar-looking names to the nearest ID.
        start=next((i for i,b in enumerate(line) if re.search(r'[a-e][1-5]',b.s,re.I)),0)
        title_boxes=line[start:]; box=(title_boxes[0].left,min(z.top for z in title_boxes),title_boxes[-1].right,max(z.bottom for z in title_boxes))
        extra=self.reruns(img,box); versions=[text]+[norm(v) for v,c,*_ in self.ocr.text_rec(extra)[0] if c>.55]
        candidates=[parsed for v in versions if (parsed:=team(v))]
        if candidates and len({tuple(self.names[a] for a in t) for t in candidates})==1 and all(reads[a]>=2 for a in candidates[0]):
            return [prefix[0].upper(),'阵容：'+'、'.join(candidates[0])]
        # Five distinct actors repeated in the body are direct evidence too. Use this only if the header
        # independently prints at least four of them; never fabricate the fifth from an edit distance.
        by_id={}
        for alias,n in reads.most_common():
            if n>=2: by_id.setdefault(self.names[alias],alias)
        if len(by_id)==5 and sum(any(a in v.lower() for v in versions) for a in by_id.values())>=4:
            return [prefix[0].upper(),'阵容：'+'、'.join(by_id.values())]
        return [CHECK+text]

    def metadata(self, img, words, sec, hy):
        np, cv = self.np, self.cv
        left = [b for b in words if b.right < sec+4 and b.y<hy]
        levels = next((b for b in left if b.s=='等级'),None)
        rank = next((b for b in left if b.s.lower()=='rank'),None)
        anchor = levels or rank
        if not anchor: return [CHECK+'找不到五列养成的列位置']
        expected = r'\d{1,3}' if levels else r'R\d+[-.]\d+'
        at = sorted((b for b in words if b.left>=sec-1 and abs(b.y-anchor.y)<(anchor.bottom-anchor.top)*.7 and re.fullmatch(expected,b.s,re.I)),key=lambda b:b.left)[:5]
        if len(at)!=5: return [CHECK+'未识别出完整五列等级或 Rank']
        centers=[(b.left+b.right)/2 for b in at]; step=float(np.median(np.diff(centers))); edge=centers[-1]+step/2
        if any(not .78*step<d<1.22*step for d in np.diff(centers)): return [CHECK+'五列养成位置不一致']
        labels=[]
        for b in left:
            s=b.s.lower(); key=None
            if s=='等级': key='等级'
            elif s=='rank': key='RANK'
            elif '战力' in s or s=='星级': key='星级'
            elif '专' in s and ('二' in s or '2' in s): key='专一/专二'
            elif 'ex' in s or ('武' in s and '饰' in s): key='装备EX'
            elif '等级' in s and len(s)>2: key='属性等级'
            elif '职' in s: key='职阶'
            if key: labels.append((b,key))
        result=[]
        for b,key in labels:
            cols=[]
            for cx in centers:
                hits=[v for v in words if v.left>=sec-2 and v.right<edge+3 and abs((v.left+v.right)/2-cx)<step*.45 and abs(v.y-b.y)<(b.bottom-b.top)*(1.0 if key=='职阶' else .65)]
                cols.append(sorted(hits,key=lambda v:v.y))
            subrows=2 if key=='职阶' else 1
            for j in range(subrows):
                val=[]; bad=False
                for k,hits in enumerate(cols):
                    s=hits[j].s if len(hits)==subrows else ''
                    if key=='RANK':s=re.sub(r'(?i)^(r\d+)\.(\d+)$',r'\1-\2',s)
                    patterns={'等级':r'\d+', 'RANK':r'[Rr]\d+-\d+', '星级':r'[1-6](?:/[1-6])?', '专一/专二':r'无|\d+(?:/\d+)?', '装备EX':r'[A-Za-z0-9]+/[A-Za-z0-9]+/[A-Za-z0-9]+', '属性等级':r'[水火风光暗]\d+', '职阶':r'\d+-\d+/\d+-\d+'}
                    if not re.fullmatch(patterns[key],s):
                        hh=b.bottom-b.top; cy=(hits[j].y if len(hits)==subrows else b.y); cx=centers[k]
                        p=img[max(0,round(cy-hh*.68)):round(cy+hh*.70),max(0,round(cx-step*.48)):round(cx+step*.48)]
                        extra=self.ocr.text_rec([cv.resize(p,None,fx=2,fy=2,interpolation=cv.INTER_CUBIC)])[0][0]
                        if re.fullmatch(patterns[key],norm(extra[0])) and extra[1]>.70: s=norm(extra[0])
                        else: bad=True
                    val.append(s or '?')
                label=key+('上' if j==0 else '下') if key=='职阶' else key
                result.append((CHECK if bad else '')+label+' '+' '.join(val))
        for line in self.lines([b for b in words if b.left>=sec-2 and b.right<edge+4 and b.y<hy]):
            text=''.join(b.s for b in line)
            if '第' in text and ('页' in text or '个' in text):
                branch=re.search(r'第\d+页.*?第\d+合流.*?交点[\[(【]?\d+级[\])】]?',text)
                master=re.search(r'第\d+个',text)
                if branch:result.append('属性技能 '+branch.group())
                if master:result.append('大师技能 '+master.group())
                if ('页' in text and not branch) or ('个' in text and not master): result.append(CHECK+text)
        return result

    def run(self, img):
        np = self.np
        h,w = img.shape[:2]; words = self.words(img); lines = self.lines(words)
        header = next((line for line in lines if sum(any(k in b.s for b in line) for k in ('秒数','角色','操作','说明'))>=3), None)
        if not header:
            raise ValueError('没有找到含时间、角色和操作列的表头；请提供完整的水平轴图。')
        hy=np.median([b.y for b in header]); first=header[0]
        sec=self.divider(img,header,first.right,header[1].left)
        # Locate genuine column dividers in this particular header, not at fixed image fractions.
        xs=[sec]+[self.divider(img,header,a.right,b.left) for a,b in zip(header[1:-1],header[2:])]
        boxes = self.cuts(img,sec,header,words)
        output=[]; records=[]; requests=[]
        reads=Counter(t for z in words if z.y>hy and z.left>=sec-2 for t in self.actor_tokens(z.s) if self.names.get(t))
        for line in lines:
            if line[0].y>=hy: break
            texts = [b.s for b in line]
            text=''.join(texts); title=re.search(r'(?:^|均伤)([A-Ea-e][1-5](?!\d).*)',text)
            if title and not output: output.extend(self.title(title.group(1),img,line,reads))
        if not output: output.append(CHECK+'未识别完整会战标题')
        output.extend(self.metadata(img,words,sec,hy))
        output.append('秒数\t角色\t操作\t说明')
        for top,bottom in boxes:
            inside=[b for b in words if top<=b.y<bottom]
            if any(b.s.lower()=='timeup' for b in inside):
                records.append(('end',[],[],[])); break
            pad=max(2,round((bottom-top)*.075)); t,bt=top+pad,bottom-pad
            if t>=bt: continue
            divs=[sec]
            for x in xs[1:]:
                v=img[t:bt,max(0,x-2):min(w,x+3)].astype('int16')
                crosses=any(b.left+3<x<b.right-3 for b in inside)
                if not crosses or v.shape[1]>1 and np.quantile(np.abs(np.diff(v,axis=1)).max(2),.16,axis=0).max()>10:
                    divs.append(x)
            bounds=[0]+divs+[w]; regions=[]
            for l,r in zip(bounds,bounds[1:]):
                # The description pane is prose, not a character sequence; some sites overlay a watermark here.
                annotation=(len(xs)>2 and l>=xs[-1]-2)
                recognized=[z for z in inside if l<=(z.left+z.right)/2<r]
                if annotation:
                    def faint(z):
                        roi=img[max(0,int(z.top)):min(h,int(z.bottom)+1),max(0,int(z.left)):min(w,int(z.right)+1)]
                        return roi.size and np.diff(np.percentile(roi,[2,98],axis=(0,1)),axis=0).max()<75
                    if recognized and bottom>h*.96 and all(z.left>w*.70 and faint(z) for z in recognized): continue
                    if not recognized:
                        roi=img[t:bt,l+2:r-2]
                        if roi.size and np.diff(np.percentile(roi,[.6,99.4],axis=(0,1)),axis=0).max()<35: continue
                parts=self.snippets(img,l+2,r-2,t,bt); group=[]; used=set()
                for xx,yy,xxx,yyy,patch in parts:
                    height=yyy-yy
                    nearby=[z for z in recognized if abs((z.left+z.right-xx-xxx)/2)<max(height*.7,(xxx-xx)*.12) and abs(z.y-(yy+yyy)/2)<height*.72 and max(z.right-z.left,xxx-xx)/max(1,min(z.right-z.left,xxx-xx))<1.7]
                    if nearby: used.add(max(nearby,key=lambda z:z.p))
                    group.append((xx,yy,xxx,yyy,len(requests)));requests.append(patch)
                for z in recognized:
                    if z in used: continue
                    # If ink crops cover a different part or combine two lines, prefer the intact detected word.
                    overlapped=[item for item in group if min(item[2],z.right)-max(item[0],z.left)>.52*min(z.right-z.left,item[2]-item[0]) and min(item[3],z.bottom)-max(item[1],z.top)>.42*min(z.bottom-z.top,item[3]-item[1])]
                    if overlapped and all((a[2]-a[0])<.74*(z.right-z.left) for a in overlapped):
                        combined=''.join(zz.s for zz in recognized if zz in used and z.left<=zz.left<z.right)
                        if combined: continue
                    for item in overlapped: group.remove(item)
                    group.append((z.left,z.top,z.right,z.bottom,z))
                group.sort(key=lambda item:(round((item[1]+item[3])/2/max(1,(bottom-top)*.18)),item[0]))
                regions.append(group)
            if not any(regions) and not inside: continue
            records.append(('row',regions,inside,(top,bottom)))
        rec = self.ocr.text_rec(requests)[0] if requests else []
        last=90
        reads=Counter(t for z in words if z.y>hy and z.left>=sec-2 for t in self.actor_tokens(z.s) if self.names.get(t))
        membership={self.names[t] for t,c in reads.items() if c>=2}
        known={t for t,id_ in self.names.items() if id_ in membership} if len(membership)==5 else set()
        def fits(s):
            tokens=self.actor_tokens(s)
            return bool(tokens) and bool(known) and all(t in known for t in tokens)
        for kind, regions, inside, interval in records:
            if kind=='end': output.append('TIME UP');break
            cells=[]; doubt=False
            for k,reg in enumerate(regions):
                pieces=[]
                for l,t,r,b,i in reg:
                    if isinstance(i,Word): s,p=i.s,i.p
                    else: s,p=norm(rec[i][0]),float(rec[i][1])
                    height=b-t
                    # A second reading with the detector's natural crop is independent of the ink crop.
                    hits=[z for z in inside if abs((z.left+z.right-l-r)/2)<max(height*.6,(r-l)*.1) and abs(z.y-(t+b)/2)<height*.65 and max(z.right-z.left,r-l)/max(1,min(z.right-z.left,r-l))<1.65]
                    opts=[(s,p)]; z=max(hits,key=lambda v:v.p) if hits else None
                    if z is not None and not isinstance(i,Word): opts.append((z.s,z.p))
                    keys=[self.notation(v) for v,c in opts]; tokens=self.actor_tokens(s)
                    missing=bool(tokens and known and any(t not in known and re.fullmatch(r'[\u3400-\u9fff]+',t) for t in tokens))
                    disputed=len(set(keys))>1
                    if p<.76 or disputed or missing:
                        ctx=(z.left,z.top,z.right,z.bottom) if z is not None else (l,t,r,b)
                        extra=self.reruns(img,ctx)
                        if extra: opts.extend((norm(v),float(c)) for v,c,*_ in self.ocr.text_rec(extra)[0])
                    count=Counter(self.notation(v) for v,c in opts if v and c>.40)
                    def order(o):
                        val,conf=o; voters=count[self.notation(val)]
                        return (fits(val) and voters>=2, voters>=2, voters, conf)
                    s,p=max(opts,key=order); ns=self.notation(s); voters=count[ns]
                    if not s or p<(.64 if k==0 else .66) and voters<2: doubt=True
                    if k and (disputed or p<.78) and voters<2: doubt=True
                    if k and known:
                        for token in self.actor_tokens(s):
                            # A near-spelling is never silently replaced by a roster name.
                            if token not in known and re.fullmatch('[\u3400-\u9fff]+',token) and any(abs(len(a)-len(token))<=1 and len(token)>=2 and (token in a or sum(c==d for c,d in zip(a,token))>=len(token)-1) for a in reads): doubt=True
                    if re.search(r'[o0x×〇○]{5}',ns): s=re.sub(r'(?i)(?<![o0x×〇○])([o0x×〇○]{5})(?![o0x×〇○])',lambda m:self.notation(m[1]),s)
                    if l>=xs[-1]-2 and len(xs)>2 and not re.search(r'容错|错\d+[f帧]|^[0ox×〇○]{5}(?:[开关]auto)?$|^[开关]auto$',self.notation(s)):
                        if not any(re.search(r'容错|错\d+[f帧]',self.notation(v.s)) for v in inside if v.left>=xs[-1]-2): doubt=True
                    pieces.append(s.replace('丨','|'))
                cells.append(''.join(pieces))
            clock=cells[0]; action='|'.join(x for x in cells[1:] if x)
            if not clock and not action: continue
            if CLOCK.fullmatch(clock):
                n=int(clock.replace(':','')); seconds=n//100*60+n%100 if len(clock.replace(':',''))==3 or ':' in clock else n
                if n>=100 and n%100>=60 or seconds>last: doubt=True
                last=seconds
            elif clock not in ('开局','开场'): doubt=True
            if not action: doubt=True
            output.append((CHECK if doubt else '')+clock+'\t'+action)
        if len(records)<2: raise ValueError('没有识别到完整时间表。')
        return '\n'.join(output)


def main():
    from PIL import Image, ImageGrab
    path=os.environ.get('GUILD_AXIS_IMAGE','')
    if path:
        p=Path(path).absolute()
        if str(p).startswith('\\\\') or p.suffix.lower() not in ('.png','.jpg','.jpeg') or p.stat().st_size>32*1024*1024:
            raise ValueError('请选择本机 32 MB 以内的 PNG/JPEG。')
        src=Image.open(p,formats=('PNG','JPEG'))
    else:
        src=ImageGrab.grabclipboard()
        if not isinstance(src,Image.Image): raise ValueError('剪贴板没有位图，请复制图片本身。')
    with src:
        if src.width*src.height>24_000_000: raise ValueError('图片太大，请先裁剪。')
        reader=Reader()
        image=reader.cv.cvtColor(reader.np.asarray(src.convert('RGB')),reader.cv.COLOR_RGB2BGR)
        # Keep table geometry and OCR cost bounded even for tall mobile captures.
        ratio=min(1,1900/image.shape[1],3000/image.shape[0])
        if ratio<1:image=reader.cv.resize(image,None,fx=ratio,fy=ratio,interpolation=reader.cv.INTER_AREA)
        return reader.run(image)


if __name__=='__main__':
    sys.stdout.reconfigure(encoding='utf-8'); sys.stderr.reconfigure(encoding='utf-8')
    try:
        print(main())
    except Exception as exc:
        print('离线图文轴识别失败：'+str(exc),file=sys.stderr)
        sys.exit(1)
