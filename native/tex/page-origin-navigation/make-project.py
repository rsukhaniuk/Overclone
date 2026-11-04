#!/usr/bin/env python3
"""Synthetic origin-domain fixtures; no rewriting the unchanged portable suite."""
import argparse,shutil
from pathlib import Path
p=argparse.ArgumentParser();p.add_argument('--kind',choices=['dvi','xe','lua'],required=True);p.add_argument('--out',type=Path,required=True)
p.add_argument('--view',action='store_true');p.add_argument('--mag1200',action='store_true');p.add_argument('--first-attributes')
p.add_argument('--extra-tex',default='');p.add_argument('--final-tex',default='');p.add_argument('--no-media',action='store_true')
p.add_argument('--repeat-put',action='store_true');p.add_argument('--lua-table',action='store_true');a=p.parse_args()
if a.out.exists():raise SystemExit('fresh fixture directory required')
root=a.out/'тези/my thesis.tex';root.parent.mkdir(parents=True);include=a.out/'глави/chapter one.tex';include.parent.mkdir()
include.write_text('\\hbox{\\kern40bp INCLUDEMARK X}\\kern12bp\n',encoding='utf8')
head=('\\mag=1200\n'if a.mag1200 else'')+'\\documentclass{article}\n'
if a.kind!='dvi':head+='\\usepackage{fontspec}\\setmainfont{DejaVu Serif}\n'
head+='\\usepackage{graphicx}\n\\paperwidth=600bp\\paperheight=800bp\\hoffset=-70bp\n'
if a.kind=='lua':head+='\\pagewidth=600bp\\pageheight=800bp\n\\def\\OriginMedia#1{\\pdfvariable pageattr{#1}}\n'
else:head+='\\def\\OriginMedia#1{\\special{pdf:put @thispage << #1 >>}}\n'
if a.lua_table:
 if a.kind!='lua':raise SystemExit('Lua table route is Lua-only')
 head=head.replace('\\pdfvariable pageattr{#1}','\\directlua{pdf.setpageattributes([=[#1]=])}')
head+='\\begin{document}\n% Синтетичний домен походження, е́ 😀; complete filenames remain unchanged.\n'+a.extra_tex+'\n'
for i,box in enumerate(['12 18 612 818','-24 -30 576 770','-18 24 582 824','30 -18 630 782'],1):
 if a.mag1200:
  try:box=' '.join(format(float(x)*1.2,'.12g')for x in box.split())
  except ValueError as error:raise AssertionError('invalid synthetic magnified rectangle')from error
 attrs=''if a.no_media else'/MediaBox ['+box+']'
 if a.view:
  try:values=[float(x)for x in box.split()]
  except ValueError as error:raise AssertionError('invalid synthetic fixture rectangle')from error
  crop=[values[0]+5,values[1]+5,values[2]-5,values[3]-5]
  attrs+=' /CropBox ['+' '.join(map(str,crop))+'] /Rotate '+str((i-1)*90)
 if i==1 and a.first_attributes is not None:attrs=a.first_attributes
 call='\\OriginMedia{'+attrs+'}\n'
 if a.kind=='lua':head+=call
 head+='\\shipout\\vbox to '+('600'if a.mag1200 else'740')+'bp{%\n'
 if a.kind!='lua':head+=call*(2 if a.repeat_put and i==1 else 1)
 head+=r'''
\kern-10bp
\hbox{\kern40bp ROOTBEFORE PAGEWORD}\kern10bp
\hbox{\kern40bp\includegraphics[width=12mm]{figure.png}}\kern10bp
\hbox{\kern40bp ROOTAFTER X}\kern10bp
\input{глави/chapter one.tex}
\vfill
\hbox{\kern40bp LEFTEDGE X}\kern5bp
\hbox{\kern470bp RIGHTEDGE X}\kern70bp
}
'''
head+=a.final_tex+'\n\\end{document}\n';root.write_text(head,encoding='utf8')
shutil.copyfile(Path(__file__).resolve().parents[3]/'src/Client/wwwroot/icons/icon-192.png',a.out/'figure.png')
shutil.copyfile(Path(__file__).resolve().parents[3]/'tests/CompilerSuiteFixtures/figure.xbb',a.out/'figure.xbb')
