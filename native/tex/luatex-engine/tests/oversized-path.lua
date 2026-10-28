local f = io.open('/work/output/' .. string.rep('P',1024*1024),'wb')
assert(f == nil, 'oversized syscall path was not refused before JS decoding')
