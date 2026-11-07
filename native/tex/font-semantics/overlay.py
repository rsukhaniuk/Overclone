#!/usr/bin/env python3
"""Exact, source-only edits to TIE's flattened genuine pdfTeX WEB input."""
import hashlib


def apply(source):
    edits = []

    def replace(before, after, count=1):
        nonlocal source
        actual = source.count(before)
        if actual != count:
            raise ValueError(f'WEB preimage count {actual} != {count}: {before[:100]!r}')
        edits.append({'before': before, 'after': after, 'count': count,
                      'preimageSha256': hashlib.sha256(before.encode()).hexdigest()})
        source = source.replace(before, after)

    replace('get_avail:=p;\nend;', 'ocsemclear(p); get_avail:=p;\nend;')
    replace('begin link(#):=avail; avail:=#;', 'begin ocsemclear(#); link(#):=avail; avail:=#;')
    replace('''    @!stat incr(dyn_used);@+tats@/
    end;
  end

@ The procedure |flush_list''', '''    @!stat incr(dyn_used);@+tats@/
    end;
  ocsemclear(#);
  end

@ The procedure |flush_list''')
    replace('repeat q:=r; r:=link(r); @!stat decr(dyn_used);',
            'repeat q:=r; ocsemclear(r); r:=link(r); @!stat decr(dyn_used);')
    replace('''  begin decr(words); mem[r+words]:=mem[p+words];
  end

@ @<Case statement to copy''', '''  begin decr(words); mem[r+words]:=mem[p+words];
  end;
if is_char_node(p) then ocsemcopy(r,p)

@ @<Case statement to copy''')
    replace('''    end;
  found: link(h):=r; h:=r;''', '''    end;
  if is_char_node(p) then ocsemcopy(r,p);
  found: link(h):=r; h:=r;''')
    replace('begin restart: cur_cs:=0;', 'begin restart: cur_cs:=0; ocsemresetcurrent;')
    replace('''@<If an alignment entry has just ended, take appropriate action@>;
exit:end;''', '''@<If an alignment entry has just ended, take appropriate action@>;
exit: cur_chr:=ocsemdecode(cur_cmd,cur_chr); end;''')
    replace('primitive("char",char_num,0);@/', '''primitive("ocCyrillicI",char_given,2031689);@/
primitive("occyrillici",char_given,2031721);@/
primitive("char",char_num,0);@/''')
    replace('define(p,cur_cmd,cur_chr);', 'define(p,cur_cmd,ocsemmeaning(cur_cmd,cur_chr));')
    replace('hmode+char_num: begin scan_char_num; cur_chr:=cur_val; goto main_loop;',
            'hmode+char_num: begin scan_char_num; cur_chr:=cur_val; ocsemresetcurrent; goto main_loop;')
    replace('begin scan_char_num; cur_chr:=cur_val; goto main_loop_lookahead+1;',
            'begin scan_char_num; cur_chr:=cur_val; ocsemresetcurrent; goto main_loop_lookahead+1;')
    replace('character(lig_stack):=cur_l;@/', 'character(lig_stack):=cur_l; ocsemattach(lig_stack);@/')
    replace('cur_r:=qi(cur_chr); character(lig_stack):=cur_r;',
            'cur_r:=qi(cur_chr); character(lig_stack):=cur_r; ocsemattach(lig_stack);')
    replace('''if (cur_cmd=letter)or(cur_cmd=other_char)or(cur_cmd=char_given) then
  q:=new_character(f,cur_chr)''', '''if (cur_cmd=letter)or(cur_cmd=other_char)or(cur_cmd=char_given) then
  begin q:=new_character(f,cur_chr); if q<>null then ocsemattach(q); end''')
    replace('hn:=0;\nloop@+', 'hn:=0; ocsemhclear(0);\nloop@+')
    replace('hb:=s; incr(hn); hu[hn]:=c; hc[hn]:=hc[0]; hyf_bchar:=non_char;',
            'hb:=s; incr(hn); hu[hn]:=c; hc[hn]:=hc[0]; hyf_bchar:=non_char; ocsemhsave(hn,s);')
    replace('incr(j); hu[j]:=c; hc[j]:=hc[0];@/',
            'incr(j); hu[j]:=c; hc[j]:=hc[0]; ocsemhsave(j,q);@/')
    replace('begin append_charnode_to_t(character(p)); p:=link(p);',
            'begin append_charnode_to_t(character(p)); ocsemcopy(t,p); p:=link(p);')
    replace('else if cur_l<non_char then append_charnode_to_t(cur_l);',
            'else if cur_l<non_char then begin append_charnode_to_t(cur_l); ocsemhrestore(t,j); end;')
    replace('character(p):=qi(hu[j+1]); font(p):=hf;',
            'character(p):=qi(hu[j+1]); font(p):=hf; ocsemhrestore(p,j+1);')
    replace('else begin append_charnode_to_t(cur_r); incr(j); set_cur_r;',
            'else begin append_charnode_to_t(cur_r); ocsemhrestore(t,j+1); incr(j); set_cur_r;')
    replace('begin incr(i); c:=hu[i]; hu[i]:=hyf_char; free_avail(hyf_node);',
            'begin incr(i); c:=hu[i]; ocsemhstash(i,0); hu[i]:=hyf_char; free_avail(hyf_node);')
    replace('begin hu[i]:=c; {restore the character in the hyphen position}',
            'begin hu[i]:=c; ocsemhunstash(i,0); {restore the character in the hyphen position}')
    replace('begin decr(l); c:=hu[l]; c_loc:=l; hu[l]:=256;',
            'begin decr(l); c:=hu[l]; c_loc:=l; ocsemhstash(l,1); hu[l]:=256;')
    replace('begin hu[c_loc]:=c; c_loc:=0;',
            'begin hu[c_loc]:=c; ocsemhunstash(c_loc,1); c_loc:=0;')
    replace('ligature_node: begin flush_node_list(lig_ptr(p));',
            'ligature_node: begin ocsemreverse(lig_ptr(p)); flush_node_list(lig_ptr(p));')
    replace('begin @<If dumping is not allowed, abort@>;', '''begin @<If dumping is not allowed, abort@>;
if ocsemdumpsafe=0 then begin
  print_err("Semantic character nodes cannot be dumped");
  help1("Clear semantic boxes before dumping a private format."); succumb;
end;''')
    # Local output state, not a mutable global shared by recursive hlist_out.
    replace('@!prev_p:pointer; {one step behind |p|}',
            '@!oc_marked:boolean; {native Unicode semantic span for this output glyph}\n@!prev_p:pointer; {one step behind |p|}', 2)
    prepare = '''if p=lig_trick then oc_marked:=ocsemprepare(p,lig_ptr(link(prev_p)))<>0
  else oc_marked:=ocsemprepare(p,null)<>0;'''
    replace('''  if is_valid_char(c) then begin
      output_one_char(c);
      cur_h:=''', '''  if is_valid_char(c) then begin
      ''' + prepare + '''
      if oc_marked then begin pdf_end_string; ocsempdfbegin; end;
      output_one_char(c);
      if oc_marked then begin pdf_end_string; ocsempdfend; end;
      cur_h:=''')
    replace('''      begin if c>=qi(128) then dvi_out(set1);
      dvi_out(qo(c));@/''', '''      begin
      ''' + prepare + '''
      if oc_marked then oc_sem_dvi_special(false);
      if c>=qi(128) then dvi_out(set1);
      dvi_out(qo(c));
      if oc_marked then oc_sem_dvi_special(true);@/''')
    replace('@p procedure@?vlist_out; forward;', '''@p procedure oc_sem_dvi_special(ending:boolean);
var oc_byte_count,oc_byte_index:integer;
begin oc_byte_count:=ocsemdvilength(ending);
dvi_out(xxx4); dvi_four(oc_byte_count);
for oc_byte_index:=0 to oc_byte_count-1 do dvi_out(ocsemdvibyte(oc_byte_index));
end;

procedure@?vlist_out; forward;''')
    return source, edits
