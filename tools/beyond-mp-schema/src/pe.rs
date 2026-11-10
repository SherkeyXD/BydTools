//! Minimal PE64 mapper for `GameAssembly.dll`.
//!
//! Only section mapping and integer reads are required. CodeGen modules,
//! exports, and the import table are not loaded.

use anyhow::{bail, Context, Result};
use std::path::Path;

#[derive(Debug, Clone)]
pub struct SectionInfo {
    pub name: String,
    pub virtual_address: u32,
    pub virtual_size: u32,
    pub raw_ptr: u32,
    pub raw_size: u32,
}

#[derive(Debug)]
pub struct PeImage {
    pub image_base: u64,
    pub sections: Vec<SectionInfo>,
    pub data: Vec<u8>,
}

impl PeImage {
    pub fn from_path(path: impl AsRef<Path>) -> Result<Self> {
        let path_ref = path.as_ref();
        let data =
            std::fs::read(path_ref).with_context(|| format!("read {}", path_ref.display()))?;
        if data.len() >= 4 && &data[0..4] == b"\x7fELF" {
            bail!(
                "{} is an ELF image. This tool reads the Windows PE64 GameAssembly.dll (Endfield PC).",
                path_ref.display()
            );
        }
        Self::parse(data).with_context(|| format!("parse PE {}", path_ref.display()))
    }

    /// Build an image from an already-mapped buffer (tests and unusual layouts).
    pub fn from_parts(image_base: u64, sections: Vec<SectionInfo>, data: Vec<u8>) -> Self {
        Self {
            image_base,
            sections,
            data,
        }
    }

    pub fn parse(data: Vec<u8>) -> Result<Self> {
        if data.len() < 0x40 || &data[0..2] != b"MZ" {
            bail!("not a PE file (missing MZ header)");
        }
        let e_lfanew = read_u32(&data, 0x3c)? as usize;
        if e_lfanew + 24 > data.len() {
            bail!("PE header truncated (e_lfanew={e_lfanew:#x})");
        }
        if &data[e_lfanew..e_lfanew + 4] != b"PE\0\0" {
            bail!("missing PE signature");
        }
        let coff = e_lfanew + 4;
        let machine = read_u16(&data, coff)?;
        if machine != 0x8664 {
            bail!("expected PE32+ machine 0x8664, got {machine:#x}");
        }
        let section_count = read_u16(&data, coff + 2)? as usize;
        let opt_size = read_u16(&data, coff + 16)? as usize;
        let opt = coff + 20;
        if opt + opt_size > data.len() || opt_size < 0x18 {
            bail!("optional header truncated");
        }
        let magic = read_u16(&data, opt)?;
        if magic != 0x20b {
            bail!("expected PE32+ optional header (0x20b), got {magic:#x}");
        }
        let image_base = read_u64(&data, opt + 24)?;
        let sec_off = opt + opt_size;
        let mut sections = Vec::with_capacity(section_count);
        for i in 0..section_count {
            let o = sec_off + i * 40;
            if o + 40 > data.len() {
                bail!("section header {i} truncated");
            }
            let name_bytes = &data[o..o + 8];
            let name = String::from_utf8_lossy(name_bytes)
                .trim_end_matches('\0')
                .to_string();
            sections.push(SectionInfo {
                name,
                virtual_size: read_u32(&data, o + 8)?,
                virtual_address: read_u32(&data, o + 12)?,
                raw_size: read_u32(&data, o + 16)?,
                raw_ptr: read_u32(&data, o + 20)?,
            });
        }
        Ok(Self {
            image_base,
            sections,
            data,
        })
    }

    pub fn va_to_offset(&self, va: u64) -> Option<usize> {
        if va < self.image_base {
            return None;
        }
        let rva = va - self.image_base;
        for s in &self.sections {
            let start = s.virtual_address as u64;
            let span = s.virtual_size.max(s.raw_size) as u64;
            if span == 0 {
                continue;
            }
            let end = start + span;
            if rva >= start && rva < end {
                let delta = rva - start;
                if delta < s.raw_size as u64 {
                    let off = s.raw_ptr as usize + delta as usize;
                    if off < self.data.len() {
                        return Some(off);
                    }
                }
            }
        }
        None
    }

    pub fn offset_to_va(&self, offset: usize) -> Option<u64> {
        let offset = offset as u64;
        for s in &self.sections {
            let raw = s.raw_ptr as u64;
            let raw_end = raw + s.raw_size as u64;
            if offset >= raw && offset < raw_end {
                let delta = offset - raw;
                return Some(self.image_base + s.virtual_address as u64 + delta);
            }
        }
        None
    }

    pub fn read_u8_va(&self, va: u64) -> Option<u8> {
        let off = self.va_to_offset(va)?;
        self.data.get(off).copied()
    }

    pub fn read_u32_va(&self, va: u64) -> Option<u32> {
        let off = self.va_to_offset(va)?;
        if off + 4 > self.data.len() {
            return None;
        }
        Some(u32::from_le_bytes(self.data[off..off + 4].try_into().ok()?))
    }

    pub fn read_i32_va(&self, va: u64) -> Option<i32> {
        Some(self.read_u32_va(va)? as i32)
    }

    pub fn read_u64_va(&self, va: u64) -> Option<u64> {
        let off = self.va_to_offset(va)?;
        if off + 8 > self.data.len() {
            return None;
        }
        Some(u64::from_le_bytes(self.data[off..off + 8].try_into().ok()?))
    }

    pub fn slice_va(&self, va: u64, len: usize) -> Option<&[u8]> {
        let off = self.va_to_offset(va)?;
        self.data.get(off..off + len)
    }

    pub fn is_executable_va(&self, va: u64) -> bool {
        if va < self.image_base {
            return false;
        }
        let rva = va - self.image_base;
        self.sections.iter().any(|s| {
            (s.name == ".text" || s.name == "il2cpp")
                && (s.virtual_address as u64..s.virtual_address as u64 + s.virtual_size.max(s.raw_size) as u64)
                    .contains(&rva)
        })
    }

    pub fn read_u64_array_va(&self, va: u64, count: usize) -> Option<Vec<u64>> {
        let mut out = Vec::with_capacity(count);
        for i in 0..count {
            out.push(self.read_u64_va(va.checked_add((i as u64) * 8)?)?);
        }
        Some(out)
    }

    pub fn va_mapped(&self, va: u64) -> bool {
        self.va_to_offset(va).is_some()
    }

    pub fn section_by_name(&self, name: &str) -> Option<&SectionInfo> {
        self.sections.iter().find(|s| s.name == name)
    }
}

fn read_u16(data: &[u8], off: usize) -> Result<u16> {
    let b = data.get(off..off + 2).context("u16 out of range")?;
    Ok(u16::from_le_bytes(b.try_into().unwrap()))
}

fn read_u32(data: &[u8], off: usize) -> Result<u32> {
    let b = data.get(off..off + 4).context("u32 out of range")?;
    Ok(u32::from_le_bytes(b.try_into().unwrap()))
}

fn read_u64(data: &[u8], off: usize) -> Result<u64> {
    let b = data.get(off..off + 8).context("u64 out of range")?;
    Ok(u64::from_le_bytes(b.try_into().unwrap()))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_minimal_pe32_plus() {
        let mut data = vec![0u8; 0x200];
        data[0] = b'M';
        data[1] = b'Z';
        data[0x3c..0x40].copy_from_slice(&0x80u32.to_le_bytes());
        data[0x80..0x84].copy_from_slice(b"PE\0\0");
        data[0x84..0x86].copy_from_slice(&0x8664u16.to_le_bytes());
        data[0x86..0x88].copy_from_slice(&1u16.to_le_bytes());
        data[0x94..0x96].copy_from_slice(&0xF0u16.to_le_bytes());
        let opt = 0x80 + 24;
        data[opt..opt + 2].copy_from_slice(&0x20bu16.to_le_bytes());
        data[opt + 24..opt + 32].copy_from_slice(&0x1_8000_0000_u64.to_le_bytes());
        let sec = opt + 0xF0;
        data[sec..sec + 8].copy_from_slice(b".rdata\0\0");
        data[sec + 8..sec + 12].copy_from_slice(&0x1000u32.to_le_bytes());
        data[sec + 12..sec + 16].copy_from_slice(&0x1000u32.to_le_bytes());
        data[sec + 16..sec + 20].copy_from_slice(&0x20u32.to_le_bytes());
        data[sec + 20..sec + 24].copy_from_slice(&0x1E0u32.to_le_bytes());
        data.resize(0x200, 0);
        data[0x1E0..0x1E8].copy_from_slice(&0x1122_3344_5566_7788u64.to_le_bytes());

        let pe = PeImage::parse(data).unwrap();
        assert_eq!(pe.image_base, 0x1_8000_0000);
        assert_eq!(pe.sections.len(), 1);
        let va = 0x1_8000_0000 + 0x1000;
        assert_eq!(pe.read_u64_va(va), Some(0x1122_3344_5566_7788));
    }
}
