// Minimal STORED-only ZIP writer for tests. No deflate; CRC-32 is computed so the
// C# import path (which verifies CRC-32 against ZipArchiveEntry.Crc32) can run.
// Not production code; the app uses System.IO.Compression in C#.

const CRC = (() => {
  const t = [];
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    t[n] = c >>> 0;
  }
  return t;
})();

function crc32(buf) {
  let c = 0xffffffff;
  for (let i = 0; i < buf.length; i++) c = (c >>> 8) ^ CRC[(c ^ buf[i]) & 0xff];
  return (c ^ 0xffffffff) >>> 0;
}

// entries: array of [name, Buffer]
function buildZip(entries) {
  const parts = [];
  const central = [];
  let offset = 0;
  for (const [name, data] of entries) {
    const nameB = Buffer.from(name, "utf8");
    const crc = crc32(data);
    const local = Buffer.alloc(30);
    local.writeUInt32LE(0x04034b50, 0);
    local.writeUInt16LE(20, 4);
    local.writeUInt16LE(0, 6);
    local.writeUInt16LE(0, 8); // method 0 = stored
    local.writeUInt16LE(0, 10);
    local.writeUInt16LE(0, 12);
    local.writeUInt32LE(crc, 14);
    local.writeUInt32LE(data.length, 18);
    local.writeUInt32LE(data.length, 22);
    local.writeUInt16LE(nameB.length, 26);
    local.writeUInt16LE(0, 28);
    parts.push(local, nameB, Buffer.from(data));
    const cdir = Buffer.alloc(46 + nameB.length);
    cdir.writeUInt32LE(0x02014b50, 0);
    cdir.writeUInt16LE(20, 4);
    cdir.writeUInt16LE(20, 6);
    cdir.writeUInt16LE(0, 8);
    cdir.writeUInt16LE(0, 10);
    cdir.writeUInt16LE(0, 12);
    cdir.writeUInt16LE(0, 14);
    cdir.writeUInt32LE(crc, 16);
    cdir.writeUInt32LE(data.length, 20);
    cdir.writeUInt32LE(data.length, 24);
    cdir.writeUInt16LE(nameB.length, 28);
    cdir.writeUInt16LE(0, 30);
    cdir.writeUInt16LE(0, 32);
    cdir.writeUInt16LE(0, 34);
    cdir.writeUInt32LE(0, 36);
    cdir.writeUInt32LE(offset, 42);
    nameB.copy(cdir, 46);
    central.push(cdir);
    offset += 30 + nameB.length + data.length;
  }
  const cdBuf = Buffer.concat(central);
  const cdOffset = offset;
  const eocd = Buffer.alloc(22);
  eocd.writeUInt32LE(0x06054b50, 0); // EOCD signature
  eocd.writeUInt16LE(0, 4); // number of this disk
  eocd.writeUInt16LE(0, 6); // disk where central directory starts
  eocd.writeUInt16LE(central.length, 8); // entries on this disk
  eocd.writeUInt16LE(central.length, 10); // total entries
  eocd.writeUInt32LE(cdBuf.length, 12); // size of central directory
  eocd.writeUInt32LE(cdOffset, 16); // offset of central directory
  eocd.writeUInt16LE(0, 20); // comment length
  return Buffer.concat([...parts, cdBuf, eocd]);
}

module.exports = { buildZip, crc32 };
