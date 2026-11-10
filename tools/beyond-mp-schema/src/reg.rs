//! Locate `Il2CppMetadataRegistration` inside GameAssembly.
//!
//! Schema export needs `types` (Il2CppType**) and `fieldOffsets`. It does not
//! load CodeRegistration or per-module method pointers.

use crate::pe::PeImage;
use anyhow::{bail, Context, Result};

/// Image-base-relative RVA of Endfield PC `MetadataRegistration`.
/// Absolute VA on the usual `0x180000000` image is `0x18DA60E20`.
pub const DEFAULT_META_REG_RVA: u64 = 0x0DA6_0E20;

/// x64 / v29 `Il2CppMetadataRegistration` (size `0x80`).
#[derive(Debug, Clone)]
pub struct MetadataRegistration {
    pub va: u64,
    pub generic_classes_count: u64,
    pub generic_classes: u64,
    pub generic_insts_count: u64,
    pub generic_insts: u64,
    pub generic_method_table_count: u64,
    pub generic_method_table: u64,
    pub types_count: u64,
    pub types: u64,
    pub method_specs_count: u64,
    pub method_specs: u64,
    pub field_offsets_count: u64,
    pub field_offsets: u64,
    pub type_definitions_sizes_count: u64,
    pub type_definitions_sizes: u64,
    pub metadata_usages_count: u64,
    pub metadata_usages: u64,
}

impl MetadataRegistration {
    pub fn parse(pe: &PeImage, va: u64) -> Result<Self> {
        let r = |off: u64| {
            pe.read_u64_va(va + off)
                .with_context(|| format!("read MetadataRegistration+{off:#x} at VA {va:#x}"))
        };
        Ok(Self {
            va,
            generic_classes_count: r(0x00)?,
            generic_classes: r(0x08)?,
            generic_insts_count: r(0x10)?,
            generic_insts: r(0x18)?,
            generic_method_table_count: r(0x20)?,
            generic_method_table: r(0x28)?,
            types_count: r(0x30)?,
            types: r(0x38)?,
            method_specs_count: r(0x40)?,
            method_specs: r(0x48)?,
            field_offsets_count: r(0x50)?,
            field_offsets: r(0x58)?,
            type_definitions_sizes_count: r(0x60)?,
            type_definitions_sizes: r(0x68)?,
            metadata_usages_count: r(0x70)?,
            metadata_usages: r(0x78)?,
        })
    }

    pub fn looks_sane(&self, type_def_count: u64) -> bool {
        self.field_offsets_count == type_def_count
            && self.type_definitions_sizes_count == type_def_count
            && self.types_count > type_def_count
            && self.types_count < 5_000_000
            && self.types != 0
            && self.field_offsets != 0
    }
}

#[derive(Debug, Clone, Default)]
pub struct LocateOptions {
    /// Skip the known RVA and only scan `.rdata`.
    pub force_scan: bool,
    /// Image-base-relative RVA. `None` uses [`DEFAULT_META_REG_RVA`].
    pub known_rva: Option<u64>,
}

#[derive(Debug, Clone)]
pub struct LocatedRegistration {
    pub reg: MetadataRegistration,
    pub source: &'static str,
    pub note: Option<String>,
}

pub fn locate_metadata_registration(
    pe: &PeImage,
    type_def_count: u64,
    opts: &LocateOptions,
) -> Result<LocatedRegistration> {
    let known_rva = opts.known_rva.unwrap_or(DEFAULT_META_REG_RVA);
    if !opts.force_scan {
        let va = pe.image_base.saturating_add(known_rva);
        match MetadataRegistration::parse(pe, va) {
            Ok(reg) if reg.looks_sane(type_def_count) => {
                return Ok(LocatedRegistration {
                    reg,
                    source: "known-rva",
                    note: None,
                });
            }
            Ok(reg) => {
                let note = format!(
                    "known MetadataRegistration at {va:#x} failed sanity (fieldOffsets={}, typeDefSizes={}, types={}, expect fieldOffsets={type_def_count})",
                    reg.field_offsets_count,
                    reg.type_definitions_sizes_count,
                    reg.types_count
                );
                let reg = scan_rdata(pe, type_def_count).with_context(|| note.clone())?;
                return Ok(LocatedRegistration {
                    reg,
                    source: "scan",
                    note: Some(note),
                });
            }
            Err(err) => {
                let note = format!("known MetadataRegistration at {va:#x} unreadable: {err:#}");
                let reg = scan_rdata(pe, type_def_count).with_context(|| note.clone())?;
                return Ok(LocatedRegistration {
                    reg,
                    source: "scan",
                    note: Some(note),
                });
            }
        }
    }
    let reg = scan_rdata(pe, type_def_count)?;
    Ok(LocatedRegistration {
        reg,
        source: "scan",
        note: None,
    })
}

fn scan_rdata(pe: &PeImage, type_def_count: u64) -> Result<MetadataRegistration> {
    let rdata = pe
        .section_by_name(".rdata")
        .context("no .rdata section (cannot scan for MetadataRegistration)")?;
    let start = rdata.raw_ptr as usize;
    let end = (start + rdata.raw_size as usize).min(pe.data.len());
    if end < start + 0x80 {
        bail!(".rdata is too small to hold MetadataRegistration");
    }
    let td = type_def_count.to_le_bytes();
    let mut found: Option<MetadataRegistration> = None;
    let mut off = start;
    // `fieldOffsetsCount` sits at +0x50 and `typeDefinitionsSizesCount` at +0x60.
    while off + 0x80 <= end {
        if pe.data[off + 0x50..off + 0x58] == td && pe.data[off + 0x60..off + 0x68] == td {
            if let Some(va) = pe.offset_to_va(off) {
                if let Ok(reg) = MetadataRegistration::parse(pe, va) {
                    if reg.looks_sane(type_def_count) && pe.va_mapped(reg.types) {
                        if va == pe.image_base.saturating_add(DEFAULT_META_REG_RVA) {
                            return Ok(reg);
                        }
                        if found.is_none() {
                            found = Some(reg);
                        }
                    }
                }
            }
        }
        off += 8;
    }
    found.context(format!(
        "scan found no MetadataRegistration with fieldOffsetsCount={type_def_count}"
    ))
}

/// Instance field offsets from `MetadataRegistration.fieldOffsets`.
/// Key is `(typedef_index, field_index_within_type)`.
pub fn load_field_offsets(
    pe: &PeImage,
    field_counts: &[u16],
    reg: &MetadataRegistration,
) -> std::collections::HashMap<(usize, usize), i32> {
    let mut out = std::collections::HashMap::new();
    if reg.field_offsets == 0 || reg.field_offsets_count == 0 {
        return out;
    }
    let n = (reg.field_offsets_count as usize).min(field_counts.len());
    let Some(ptrs) = pe.read_u64_array_va(reg.field_offsets, n) else {
        return out;
    };
    for (ti, ptr) in ptrs.into_iter().enumerate() {
        if ptr == 0 || !pe.va_mapped(ptr) {
            continue;
        }
        let Some(off) = pe.va_to_offset(ptr) else {
            continue;
        };
        let count = field_counts.get(ti).copied().unwrap_or(0) as usize;
        for fi in 0..count {
            let at = off + fi * 4;
            if at + 4 > pe.data.len() {
                break;
            }
            let v = i32::from_le_bytes(pe.data[at..at + 4].try_into().unwrap());
            out.insert((ti, fi), v);
        }
    }
    out
}
