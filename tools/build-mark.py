#!/usr/bin/env python3
"""Rebuild the existing Lowfin L as a tiny color icon font (fonttools + brotli)."""
from pathlib import Path
from fontTools.fontBuilder import FontBuilder
from fontTools.pens.ttGlyphPen import TTGlyphPen
from fontTools.pens.cu2quPen import Cu2QuPen
from fontTools.pens.transformPen import TransformPen
from fontTools.svgLib.path import parse_path
from fontTools.colorLib.builder import buildCOLR, buildCPAL
root=Path(__file__).resolve().parents[1]
paths=['M0 280V78C0 36 37 15 92 0V157C92 205 63 229 0 280Z','M16 282L108 194C130 172 166 164 195 164H282C268 235 230 282 179 282Z']
glyphs={}
for name,path in zip(['left','base'],paths):
    pen=TTGlyphPen(None);parse_path(path,TransformPen(Cu2QuPen(pen,1.0,reverse_direction=True),(3,0,0,-3,89,950)));glyphs[name]=pen.glyph()
pen=TTGlyphPen(glyphs)
for name in ['left','base']:pen.addComponent(name,(1,0,0,1,0,0))
glyphs['uniEFFF']=pen.glyph();glyphs['.notdef']=TTGlyphPen(None).glyph()
fb=FontBuilder(1024,isTTF=True);fb.setupGlyphOrder(['.notdef','uniEFFF','left','base']);fb.setupCharacterMap({0xEFFF:'uniEFFF'});fb.setupGlyf(glyphs);fb.setupHorizontalMetrics({k:(1024,0) for k in glyphs});fb.setupHorizontalHeader(ascent=1024,descent=0);fb.setupNameTable({'familyName':'Lowfin Companion Mark','styleName':'Regular','uniqueFontIdentifier':'LowfinCompanionMark-v1','fullName':'Lowfin Companion Mark','psName':'LowfinCompanionMark'});fb.setupOS2(sTypoAscender=1024,sTypoDescender=0,usWinAscent=1024,usWinDescent=0);fb.setupPost();fb.setupMaxp();fb.font['COLR']=buildCOLR({'uniEFFF':[('left',0),('base',1)]});fb.font['CPAL']=buildCPAL([[(0x55/255,0x54/255,0x5f/255,1),(0x79/255,0x68/255,0x8f/255,1)]]);fb.font.flavor='woff2';fb.save(root/'src/Lowfin.Companion/Assets/lowfin-l.woff2')
(root/'src/Lowfin.Companion/Assets/lowfin-l.svg').write_text('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 282 282"><path fill="#55545f" d="'+paths[0]+'"/><path fill="#79688f" d="'+paths[1]+'"/></svg>\n')
