//! Beyond `USPC` string pool (`Data/Json/StringPool/StringPool*.byte`).
//!
//! MemoryPack strings are int32 ids: `-1` empty, `-2` null, otherwise
//! `block_index * len(StringPool0) + local_index`.

use anyhow::{bail, Context, Result};
use serde_json::Value;
use std::collections::BTreeMap;
use std::fs;
use std::path::Path;

#[derive(Debug, Clone, Default)]
pub struct StringTable {
    map: BTreeMap<i32, String>,
}

impl StringTable {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn insert(&mut self, id: i32, value: impl Into<String>) {
        self.map.insert(id, value.into());
    }

    pub fn get(&self, id: i32) -> Option<&str> {
        self.map.get(&id).map(String::as_str)
    }

    pub fn len(&self) -> usize {
        self.map.len()
    }

    pub fn is_empty(&self) -> bool {
        self.map.is_empty()
    }

    /// JSON object `{ "66099": "sm2l1m3_q#2", ... }`.
    pub fn load_json_map(&mut self, path: &Path) -> Result<usize> {
        let text = fs::read_to_string(path).with_context(|| format!("read {}", path.display()))?;
        let value: Value =
            serde_json::from_str(&text).with_context(|| format!("parse {}", path.display()))?;
        let obj = value
            .as_object()
            .context("string map must be a JSON object of id → string")?;
        let mut n = 0;
        for (k, v) in obj {
            let id: i32 = k
                .parse()
                .with_context(|| format!("string map key {k} is not an int32"))?;
            let s = v
                .as_str()
                .with_context(|| format!("string map value for {k} is not a string"))?;
            self.insert(id, s);
            n += 1;
        }
        Ok(n)
    }

    /// Load every `StringPoolN.byte` in `dir`.
    pub fn load_pool_dir(&mut self, dir: &Path) -> Result<usize> {
        let mut blocks: BTreeMap<usize, Vec<String>> = BTreeMap::new();
        for ent in fs::read_dir(dir).with_context(|| format!("read dir {}", dir.display()))? {
            let ent = ent?;
            let name = ent.file_name();
            let name = name.to_string_lossy();
            let Some(rest) = name.strip_prefix("StringPool") else {
                continue;
            };
            let Some(num) = rest.strip_suffix(".byte") else {
                continue;
            };
            if num.is_empty() || !num.chars().all(|c| c.is_ascii_digit()) {
                continue;
            }
            let index: usize = num
                .parse()
                .with_context(|| format!("pool index in {name}"))?;
            let bytes = fs::read(ent.path())?;
            let strings =
                parse_uspc(&bytes).with_context(|| format!("parse {}", ent.path().display()))?;
            blocks.insert(index, strings);
        }
        if blocks.is_empty() {
            bail!("no StringPoolN.byte files in {}", dir.display());
        }
        let Some(block0) = blocks.get(&0) else {
            bail!("StringPool0.byte is required to compute the global id stride");
        };
        let stride = block0.len();
        if stride == 0 {
            bail!("StringPool0.byte has no strings");
        }
        let mut n = 0;
        for (block, strings) in &blocks {
            for (local, s) in strings.iter().enumerate() {
                let id = (*block * stride + local) as i32;
                self.insert(id, s.clone());
                n += 1;
            }
        }
        Ok(n)
    }
}

pub fn parse_uspc(mut data: &[u8]) -> Result<Vec<String>> {
    if data.len() >= 8 && &data[..4] != b"USPC" && &data[4..8] == b"USPC" {
        data = &data[4..];
    }
    if data.len() < 24 || &data[..4] != b"USPC" {
        bail!("missing USPC magic");
    }
    let version = i32::from_le_bytes(data[4..8].try_into().unwrap());
    let flags = i32::from_le_bytes(data[8..12].try_into().unwrap());
    let count = i32::from_le_bytes(data[12..16].try_into().unwrap());
    let raw_len = i32::from_le_bytes(data[16..20].try_into().unwrap());
    let stored = i32::from_le_bytes(data[20..24].try_into().unwrap());
    if version != 1 {
        bail!("unsupported USPC version {version}");
    }
    if count < 0 || raw_len < 0 || stored < 0 {
        bail!("negative USPC header field");
    }
    let stored = stored as usize;
    let raw_len = raw_len as usize;
    if data.len() < 24 + stored {
        bail!("USPC payload truncated");
    }
    let payload = &data[24..24 + stored];
    let raw = if flags & 1 != 0 {
        lz4_block_decode(payload, raw_len)?
    } else {
        if payload.len() < raw_len {
            bail!("USPC raw payload shorter than rawLength");
        }
        payload[..raw_len].to_vec()
    };
    parse_uncompressed(&raw, count as usize)
}

fn parse_uncompressed(raw: &[u8], count: usize) -> Result<Vec<String>> {
    if raw.len() < 4 {
        bail!("USPC uncompressed payload truncated");
    }
    let inner = i32::from_le_bytes(raw[0..4].try_into().unwrap());
    if inner != count as i32 {
        bail!("USPC count mismatch header={count} payload={inner}");
    }
    let base = 4 + 4 * count;
    if raw.len() < base {
        bail!("USPC offset table truncated");
    }
    let mut out = Vec::with_capacity(count);
    for i in 0..count {
        let rel = i32::from_le_bytes(raw[4 + 4 * i..8 + 4 * i].try_into().unwrap());
        if rel < 0 {
            bail!("negative string offset");
        }
        let at = base + rel as usize;
        if at + 4 > raw.len() {
            bail!("string record {i} out of range");
        }
        let len = i32::from_le_bytes(raw[at..at + 4].try_into().unwrap());
        if len < 0 {
            bail!("negative string length");
        }
        let len = len as usize;
        let start = at + 4;
        let end = start + len;
        if end > raw.len() {
            bail!("string {i} bytes out of range");
        }
        let s = std::str::from_utf8(&raw[start..end])
            .with_context(|| format!("string {i} is not utf-8"))?;
        out.push(s.to_string());
    }
    Ok(out)
}

/// Standard LZ4 block (not a frame). Matches Beyond `UniqueStringPool.Lz4Codec`.
pub fn lz4_block_decode(src: &[u8], dst_size: usize) -> Result<Vec<u8>> {
    let mut dst = vec![0u8; dst_size];
    let mut ip = 0usize;
    let mut op = 0usize;
    let n = src.len();
    while ip < n && op < dst_size {
        let token = src[ip];
        ip += 1;
        let mut lit = (token >> 4) as usize;
        if lit == 15 {
            loop {
                if ip >= n {
                    bail!("LZ4 literal length overrun");
                }
                let b = src[ip];
                ip += 1;
                lit += b as usize;
                if b != 255 {
                    break;
                }
            }
        }
        if ip + lit > n || op + lit > dst_size {
            bail!("LZ4 literal out of range");
        }
        dst[op..op + lit].copy_from_slice(&src[ip..ip + lit]);
        ip += lit;
        op += lit;
        if ip >= n || op >= dst_size {
            break;
        }
        if ip + 2 > n {
            bail!("LZ4 offset truncated");
        }
        let offset = src[ip] as usize | ((src[ip + 1] as usize) << 8);
        ip += 2;
        if offset == 0 || offset > op {
            bail!("LZ4 invalid match offset {offset}");
        }
        let mut ml = (token & 0x0f) as usize + 4;
        if (token & 0x0f) == 15 {
            loop {
                if ip >= n {
                    bail!("LZ4 match length overrun");
                }
                let b = src[ip];
                ip += 1;
                ml += b as usize;
                if b != 255 {
                    break;
                }
            }
        }
        if op + ml > dst_size {
            bail!("LZ4 match out of range");
        }
        let mut mp = op - offset;
        for _ in 0..ml {
            dst[op] = dst[mp];
            op += 1;
            mp += 1;
        }
    }
    if op < dst_size {
        dst.truncate(op);
    }
    Ok(dst)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn lz4_literal_only() {
        let out = lz4_block_decode(b"\x20hi", 2).unwrap();
        assert_eq!(out, b"hi");
    }

    #[test]
    fn uspc_roundtrip_two_strings() {
        let payload = uncompressed_payload(&["ab", "cde"]);
        let mut file = Vec::new();
        file.extend_from_slice(b"USPC");
        file.extend_from_slice(&1i32.to_le_bytes());
        file.extend_from_slice(&0i32.to_le_bytes());
        file.extend_from_slice(&2i32.to_le_bytes());
        file.extend_from_slice(&(payload.len() as i32).to_le_bytes());
        file.extend_from_slice(&(payload.len() as i32).to_le_bytes());
        file.extend_from_slice(&payload);
        let strings = parse_uspc(&file).unwrap();
        assert_eq!(strings, vec!["ab".to_string(), "cde".to_string()]);

        let mut prefixed = 0i32.to_le_bytes().to_vec();
        prefixed.extend_from_slice(&file);
        assert_eq!(parse_uspc(&prefixed).unwrap(), strings);
    }

    fn uncompressed_payload(strings: &[&str]) -> Vec<u8> {
        let mut raw = Vec::new();
        raw.extend_from_slice(&(strings.len() as i32).to_le_bytes());
        let mut records = Vec::new();
        let mut offsets = Vec::new();
        for s in strings {
            offsets.push(records.len() as i32);
            records.extend_from_slice(&(s.len() as i32).to_le_bytes());
            records.extend_from_slice(s.as_bytes());
        }
        for off in offsets {
            raw.extend_from_slice(&off.to_le_bytes());
        }
        raw.extend_from_slice(&records);
        raw
    }
}
