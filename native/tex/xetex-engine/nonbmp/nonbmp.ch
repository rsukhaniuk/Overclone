% Candidate-only Xe filename scalar/UTF16/UTF8 boundary.
% Apply after the accepted complete Xe maps change file, then regenerate C.
@x
@p function more_name(@!c:ASCII_code):boolean;
@y
@p function more_name(@!c:UnicodeScalar):boolean;
@z

@x
else  begin str_room(1); append_char(c); {contribute |c| to the current string}
@y
else  begin
  if c>biggest_char then begin
    str_room(2);
    append_char(@"D800+(c-@"10000) div @"400);
    append_char(@"DC00+(c-@"10000) mod @"400);
  end else begin str_room(1); append_char(c); end;
@z

@x
@d append_to_name(#)==begin c:=#; incr(k);
  if k<=file_name_size then begin
      if (c < 128) then name_of_file[k]:=c
      else if (c < @"800) then begin
        name_of_file[k]:=@"C0 + c div @"40; incr(k);
        name_of_file[k]:=@"80 + c mod @"40;
      end else begin
		name_of_file[k]:=@"E0 + c div @"1000; incr(k);
		name_of_file[k]:=@"80 + (c mod @"1000) div @"40; incr(k);
		name_of_file[k]:=@"80 + (c mod @"1000) mod @"40;
      end
    end
  end
@y
@d append_to_name(#)==begin c:=#; incr(k);
  if k<=file_name_size then begin
    if c<@"80 then name_of_file[k]:=c
    else if c<@"800 then begin
      name_of_file[k]:=@"C0+c div @"40; incr(k);
      name_of_file[k]:=@"80+c mod @"40;
    end else if c<@"10000 then begin
      name_of_file[k]:=@"E0+c div @"1000; incr(k);
      name_of_file[k]:=@"80+(c mod @"1000) div @"40; incr(k);
      name_of_file[k]:=@"80+c mod @"40;
    end else begin
      name_of_file[k]:=@"F0+c div @"40000; incr(k);
      name_of_file[k]:=@"80+(c mod @"40000) div @"1000; incr(k);
      name_of_file[k]:=@"80+(c mod @"1000) div @"40; incr(k);
      name_of_file[k]:=@"80+c mod @"40;
    end;
  end;
end

@d append_name_string(#)==begin
  j:=str_start_macro(#);
  while j<str_start_macro(#+1) do begin
    c:=so(str_pool[j]); incr(j);
    if (c>=@"D800)and(c<=@"DBFF)and(j<str_start_macro(#+1)) then
      if (str_pool[j]>=@"DC00)and(str_pool[j]<=@"DFFF) then begin
        c:=@"10000+(c-@"D800)*@"400+str_pool[j]-@"DC00;
        incr(j);
      end;
    append_to_name(c);
  end;
end
@z

@x
@p procedure pack_file_name(@!n,@!a,@!e:str_number);
var k:integer; {number of positions filled in |name_of_file|}
@!c: ASCII_code; {character being packed}
@y
@p procedure pack_file_name(@!n,@!a,@!e:str_number);
var k:integer; {number of positions filled in |name_of_file|}
@!c: UnicodeScalar; {decoded scalar, not an isolated UTF16 code unit}
@z

@x
for j:=str_start_macro(a) to str_start_macro(a+1)-1 do append_to_name(so(str_pool[j]));
for j:=str_start_macro(n) to str_start_macro(n+1)-1 do append_to_name(so(str_pool[j]));
for j:=str_start_macro(e) to str_start_macro(e+1)-1 do append_to_name(so(str_pool[j]));
@y
append_name_string(a);
append_name_string(n);
append_name_string(e);
@z

@x
@p procedure pack_buffered_name(@!n:small_number;@!a,@!b:integer);
var k:integer; {number of positions filled in |name_of_file|}
@!c: ASCII_code; {character being packed}
@y
@p procedure pack_buffered_name(@!n:small_number;@!a,@!b:integer);
var k:integer; {number of positions filled in |name_of_file|}
@!c: UnicodeScalar; {the input buffer already contains Unicode scalars}
@z

@x
name_of_file := xmalloc_array (UTF8_code, n+(b-a+1)+format_ext_length+1);
@y
name_of_file := xmalloc_array (UTF8_code, (n+(b-a+1)+format_ext_length)*4+1);
@z

@x
loop@+begin if (cur_cmd>other_char)or(cur_chr>biggest_char) then
@y
loop@+begin if (cur_cmd>other_char)or(cur_chr>biggest_usv) then
@z
