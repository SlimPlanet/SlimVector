(function initializeSlimVectorMessagePack(global) {
  'use strict';

  const textEncoder = new TextEncoder();
  const textDecoder = new TextDecoder('utf-8', { fatal: true });

  class Writer {
    constructor() {
      this.bytes = [];
    }

    push(...values) {
      this.bytes.push(...values.map(value => value & 0xff));
    }

    uint16(value) {
      this.push(value >>> 8, value);
    }

    uint32(value) {
      this.push(value >>> 24, value >>> 16, value >>> 8, value);
    }

    uint64(value) {
      const integer = BigInt(value);
      for (let shift = 56n; shift >= 0n; shift -= 8n) {
        this.push(Number((integer >> shift) & 0xffn));
      }
    }

    float64(value) {
      const buffer = new ArrayBuffer(8);
      new DataView(buffer).setFloat64(0, value, false);
      this.push(...new Uint8Array(buffer));
    }

    raw(value) {
      this.push(...value);
    }

    finish() {
      return Uint8Array.from(this.bytes);
    }
  }

  function writeString(writer, value) {
    const encoded = textEncoder.encode(value);
    if (encoded.length < 32) writer.push(0xa0 | encoded.length);
    else if (encoded.length <= 0xff) writer.push(0xd9, encoded.length);
    else if (encoded.length <= 0xffff) {
      writer.push(0xda);
      writer.uint16(encoded.length);
    } else {
      writer.push(0xdb);
      writer.uint32(encoded.length);
    }
    writer.raw(encoded);
  }

  function writeArray(writer, value) {
    if (value.length < 16) writer.push(0x90 | value.length);
    else if (value.length <= 0xffff) {
      writer.push(0xdc);
      writer.uint16(value.length);
    } else {
      writer.push(0xdd);
      writer.uint32(value.length);
    }
    value.forEach(item => writeValue(writer, item));
  }

  function writeMap(writer, value) {
    const entries = Object.entries(value).filter(([, item]) => item !== undefined);
    if (entries.length < 16) writer.push(0x80 | entries.length);
    else if (entries.length <= 0xffff) {
      writer.push(0xde);
      writer.uint16(entries.length);
    } else {
      writer.push(0xdf);
      writer.uint32(entries.length);
    }
    entries.forEach(([key, item]) => {
      writeString(writer, key);
      writeValue(writer, item);
    });
  }

  function writeInteger(writer, value) {
    if (value >= 0) {
      if (value < 0x80) writer.push(value);
      else if (value <= 0xff) writer.push(0xcc, value);
      else if (value <= 0xffff) {
        writer.push(0xcd);
        writer.uint16(value);
      } else if (value <= 0xffffffff) {
        writer.push(0xce);
        writer.uint32(value);
      } else {
        writer.push(0xcf);
        writer.uint64(value);
      }
      return;
    }

    if (value >= -32) writer.push(0x100 + value);
    else if (value >= -128) writer.push(0xd0, value);
    else if (value >= -32768) {
      writer.push(0xd1);
      writer.uint16(value);
    } else if (value >= -2147483648) {
      writer.push(0xd2);
      writer.uint32(value);
    } else {
      writer.push(0xd3);
      writer.uint64(BigInt.asUintN(64, BigInt(value)));
    }
  }

  function writeBinary(writer, value) {
    if (value.length <= 0xff) writer.push(0xc4, value.length);
    else if (value.length <= 0xffff) {
      writer.push(0xc5);
      writer.uint16(value.length);
    } else {
      writer.push(0xc6);
      writer.uint32(value.length);
    }
    writer.raw(value);
  }

  function writeValue(writer, value) {
    if (value === null) {
      writer.push(0xc0);
    } else if (value === false) {
      writer.push(0xc2);
    } else if (value === true) {
      writer.push(0xc3);
    } else if (typeof value === 'number') {
      if (!Number.isFinite(value)) throw new TypeError('MessagePack refuse les nombres non finis.');
      if (Number.isSafeInteger(value)) writeInteger(writer, value);
      else {
        writer.push(0xcb);
        writer.float64(value);
      }
    } else if (typeof value === 'bigint') {
      if (value >= 0n) {
        writer.push(0xcf);
        writer.uint64(value);
      } else {
        writer.push(0xd3);
        writer.uint64(BigInt.asUintN(64, value));
      }
    } else if (typeof value === 'string') {
      writeString(writer, value);
    } else if (value instanceof Uint8Array) {
      writeBinary(writer, value);
    } else if (Array.isArray(value)) {
      writeArray(writer, value);
    } else if (typeof value === 'object') {
      writeMap(writer, value);
    } else {
      throw new TypeError(`Type MessagePack non pris en charge : ${typeof value}.`);
    }
  }

  function encode(value) {
    const writer = new Writer();
    writeValue(writer, value);
    return writer.finish();
  }

  class Reader {
    constructor(value) {
      const bytes = value instanceof Uint8Array
        ? value
        : value instanceof ArrayBuffer
          ? new Uint8Array(value)
          : ArrayBuffer.isView(value)
            ? new Uint8Array(value.buffer, value.byteOffset, value.byteLength)
            : null;
      if (!bytes) throw new TypeError('Un ArrayBuffer ou Uint8Array est requis.');
      this.bytes = bytes;
      this.view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
      this.offset = 0;
    }

    ensure(length) {
      if (this.offset + length > this.bytes.length) {
        throw new RangeError('Charge utile MessagePack tronquée.');
      }
    }

    uint8() {
      this.ensure(1);
      return this.bytes[this.offset++];
    }

    int8() {
      this.ensure(1);
      return this.view.getInt8(this.offset++);
    }

    uint16() {
      this.ensure(2);
      const value = this.view.getUint16(this.offset, false);
      this.offset += 2;
      return value;
    }

    int16() {
      this.ensure(2);
      const value = this.view.getInt16(this.offset, false);
      this.offset += 2;
      return value;
    }

    uint32() {
      this.ensure(4);
      const value = this.view.getUint32(this.offset, false);
      this.offset += 4;
      return value;
    }

    int32() {
      this.ensure(4);
      const value = this.view.getInt32(this.offset, false);
      this.offset += 4;
      return value;
    }

    uint64() {
      this.ensure(8);
      const value = this.view.getBigUint64(this.offset, false);
      this.offset += 8;
      return Number(value);
    }

    int64() {
      this.ensure(8);
      const value = this.view.getBigInt64(this.offset, false);
      this.offset += 8;
      return Number(value);
    }

    float32() {
      this.ensure(4);
      const value = this.view.getFloat32(this.offset, false);
      this.offset += 4;
      return value;
    }

    float64() {
      this.ensure(8);
      const value = this.view.getFloat64(this.offset, false);
      this.offset += 8;
      return value;
    }

    raw(length) {
      this.ensure(length);
      const value = this.bytes.subarray(this.offset, this.offset + length);
      this.offset += length;
      return value;
    }

    string(length) {
      return textDecoder.decode(this.raw(length));
    }
  }

  function readArray(reader, length, depth) {
    if (length > reader.bytes.length - reader.offset) {
      throw new RangeError('Longueur de tableau MessagePack invalide.');
    }
    const result = new Array(length);
    for (let index = 0; index < length; index++) result[index] = readValue(reader, depth);
    return result;
  }

  function readMap(reader, length, depth) {
    if (length > Math.floor((reader.bytes.length - reader.offset) / 2)) {
      throw new RangeError('Longueur de table MessagePack invalide.');
    }
    const result = Object.create(null);
    for (let index = 0; index < length; index++) {
      const key = readValue(reader, depth);
      if (typeof key !== 'string') throw new TypeError('Les clés MessagePack doivent être des chaînes.');
      result[key] = readValue(reader, depth);
    }
    return result;
  }

  function readValue(reader, depth = 0) {
    if (depth > 64) throw new RangeError('Profondeur MessagePack maximale dépassée.');
    const prefix = reader.uint8();
    if (prefix <= 0x7f) return prefix;
    if (prefix >= 0xe0) return prefix - 0x100;
    if ((prefix & 0xe0) === 0xa0) return reader.string(prefix & 0x1f);
    if ((prefix & 0xf0) === 0x90) return readArray(reader, prefix & 0x0f, depth + 1);
    if ((prefix & 0xf0) === 0x80) return readMap(reader, prefix & 0x0f, depth + 1);

    switch (prefix) {
      case 0xc0: return null;
      case 0xc2: return false;
      case 0xc3: return true;
      case 0xc4: return reader.raw(reader.uint8());
      case 0xc5: return reader.raw(reader.uint16());
      case 0xc6: return reader.raw(reader.uint32());
      case 0xca: return reader.float32();
      case 0xcb: return reader.float64();
      case 0xcc: return reader.uint8();
      case 0xcd: return reader.uint16();
      case 0xce: return reader.uint32();
      case 0xcf: return reader.uint64();
      case 0xd0: return reader.int8();
      case 0xd1: return reader.int16();
      case 0xd2: return reader.int32();
      case 0xd3: return reader.int64();
      case 0xd9: return reader.string(reader.uint8());
      case 0xda: return reader.string(reader.uint16());
      case 0xdb: return reader.string(reader.uint32());
      case 0xdc: return readArray(reader, reader.uint16(), depth + 1);
      case 0xdd: return readArray(reader, reader.uint32(), depth + 1);
      case 0xde: return readMap(reader, reader.uint16(), depth + 1);
      case 0xdf: return readMap(reader, reader.uint32(), depth + 1);
      default: throw new TypeError(`Préfixe MessagePack non pris en charge : 0x${prefix.toString(16)}.`);
    }
  }

  function decode(value) {
    const reader = new Reader(value);
    const result = readValue(reader);
    if (reader.offset !== reader.bytes.length) {
      throw new RangeError('La charge utile MessagePack contient des données supplémentaires.');
    }
    return result;
  }

  const codec = Object.freeze({ encode, decode });
  if (typeof module === 'object' && module.exports) module.exports = codec;
  else global.SlimVectorMessagePack = codec;
})(typeof globalThis === 'object' ? globalThis : window);
