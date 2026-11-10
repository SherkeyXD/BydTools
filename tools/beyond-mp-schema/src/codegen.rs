//! Map metadata methods onto GameAssembly function VAs via CodeGenModules.
//!
//! Each image name string is found in the PE, then the CodeGenModule that
//! points at it. `methodPointers[i]` is the i-th method of that image in
//! TypeDef order.

use crate::metadata::Metadata;
use crate::pe::PeImage;
use anyhow::Result;
use std::collections::HashMap;

#[derive(Debug, Clone)]
pub struct MethodVa {
    pub typedef_index: usize,
    pub method_index: usize,
    pub name: String,
    pub declaring_name: String,
    pub va: u64,
}

#[derive(Debug, Default)]
pub struct CodeMap {
    pub methods: Vec<MethodVa>,
    pub va_by_method_index: HashMap<usize, u64>,
    pub modules: Vec<String>,
}

pub fn load_code_map(pe: &PeImage, meta: &Metadata) -> Result<CodeMap> {
    let mut out = CodeMap::default();
    for img in &meta.images {
        let name = meta.image_name(img).unwrap_or_default();
        if name.is_empty() {
            continue;
        }
        let Some(module_va) = find_codegen_module(pe, &name) else {
            continue;
        };
        let count = pe.read_u32_va(module_va + 8).unwrap_or(0) as usize;
        let ptrs_va = pe.read_u64_va(module_va + 16).unwrap_or(0);
        if count == 0 || ptrs_va == 0 {
            continue;
        }
        let Some(ptrs) = pe.read_u64_array_va(ptrs_va, count) else {
            continue;
        };
        let start = img.type_start.max(0) as usize;
        let end = (start + img.type_count as usize).min(meta.type_defs.len());
        let mut meta_methods = 0usize;
        for ti in start..end {
            meta_methods += meta.type_defs[ti].method_count as usize;
        }
        eprintln!(
            "codegen {} pointers={} metadata_methods={}",
            name, count, meta_methods
        );
        out.modules.push(name.clone());

        let mut slot = 0usize;
        for ti in start..end {
            let td = &meta.type_defs[ti];
            if td.method_count == 0 || td.method_index < 0 {
                continue;
            }
            let declaring = meta.type_name(td).unwrap_or_else(|_| "?".into());
            let m0 = td.method_index as usize;
            for local in 0..td.method_count as usize {
                if slot >= ptrs.len() {
                    break;
                }
                let mi = m0 + local;
                let mname = meta
                    .methods
                    .get(mi)
                    .and_then(|m| meta.string(m.name_index).ok())
                    .unwrap_or_default();
                let va = ptrs[slot];
                slot += 1;
                if va == 0 || !pe.is_executable_va(va) {
                    continue;
                }
                out.va_by_method_index.insert(mi, va);
                out.methods.push(MethodVa {
                    typedef_index: ti,
                    method_index: mi,
                    name: mname,
                    declaring_name: declaring.clone(),
                    va,
                });
            }
        }
    }
    Ok(out)
}

fn find_codegen_module(pe: &PeImage, image_name: &str) -> Option<u64> {
    let needle = [image_name.as_bytes(), &[0]].concat();
    for sec_name in [".rdata", ".data", "il2cpp"] {
        let Some(sec) = pe.section_by_name(sec_name) else {
            continue;
        };
        let start = sec.raw_ptr as usize;
        let end = (start + sec.raw_size as usize).min(pe.data.len());
        let hay = &pe.data[start..end];
        let mut from = 0usize;
        while let Some(rel) = find_bytes(&hay[from..], &needle) {
            let off = start + from + rel;
            let Some(str_va) = pe.offset_to_va(off) else {
                from += rel + 1;
                continue;
            };
            if let Some(module_va) = find_pointer_to(pe, str_va) {
                return Some(module_va);
            }
            from += rel + 1;
        }
    }
    None
}

fn find_pointer_to(pe: &PeImage, str_va: u64) -> Option<u64> {
    let ptr = str_va.to_le_bytes();
    for sec_name in [".rdata", ".data"] {
        let Some(sec) = pe.section_by_name(sec_name) else {
            continue;
        };
        let start = sec.raw_ptr as usize;
        let end = (start + sec.raw_size as usize).min(pe.data.len());
        let hay = &pe.data[start..end];
        let mut from = 0usize;
        while let Some(rel) = find_bytes(&hay[from..], &ptr) {
            let poff = start + from + rel;
            if let Some(module_va) = pe.offset_to_va(poff) {
                let count = pe.read_u32_va(module_va + 8).unwrap_or(0);
                let ptrs = pe.read_u64_va(module_va + 16).unwrap_or(0);
                if count > 0 && count < 5_000_000 && pe.va_mapped(ptrs) {
                    return Some(module_va);
                }
            }
            from += rel + 1;
        }
    }
    None
}

fn find_bytes(hay: &[u8], needle: &[u8]) -> Option<usize> {
    hay.windows(needle.len()).position(|w| w == needle)
}
