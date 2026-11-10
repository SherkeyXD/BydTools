//! Recover union jumptables from Beyond `*ForMemoryPack` native methods.
//!
//! Endfield does not ship `[MemoryPackUnion]`. Tags live in a jumptable:
//! `cmp tag, MAX; ja; mov r32, [base+tag*4+RVA]; add r, base; jmp r`.
//! IL2CPP places the large maps on wrapper `.cctor`; `Formatter.Deserialize`
//! is often a short thunk. The table base is `r15` (image base) or a
//! RIP-relative `lea`. Each case loads a TypeInfo slot that maps to a TypeDef.

use crate::codegen::CodeMap;
use crate::metadata::Metadata;
use crate::pe::PeImage;
use crate::typesys::TypeSystem;
use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, HashSet};

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct UnionTag {
    pub tag: i32,
    pub type_name: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub wrapper_type: Option<String>,
    pub typedef_index: usize,
    pub case_va: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct UnionTable {
    pub static_type: String,
    pub wrapper_type: String,
    pub formatter_type: String,
    pub deserialize_va: String,
    pub max_tag: i32,
    pub jumptable_rva: String,
    pub tags: BTreeMap<String, String>,
    pub evidence: Vec<UnionTag>,
    pub skipped: Vec<i32>,
    pub status: String,
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
pub struct NativeFacts {
    pub unions: Vec<UnionTable>,
    pub member_counts: BTreeMap<String, i32>,
}

struct PdataFn {
    begin_rva: u32,
    end_rva: u32,
}

/// IL2CPP cold-split entries are a short thunk. Larger `.pdata` already
/// covers the Deserialize body, including the jumptable.
const COLD_ENTRY_MAX: u64 = 0x100;

pub fn analyze(
    pe: &PeImage,
    meta: &Metadata,
    ts: &mut TypeSystem<'_>,
    code: &CodeMap,
    wrapper_by_target_td: &BTreeMap<usize, String>,
    target_name_by_td: &BTreeMap<usize, String>,
    target_name_by_wrapper_td: &BTreeMap<usize, String>,
) -> NativeFacts {
    let pdata = load_pdata(pe);
    let mut facts = NativeFacts::default();
    let mut seen_va = HashSet::new();
    let mut by_table: BTreeMap<String, UnionTable> = BTreeMap::new();

    for method in &code.methods {
        if !is_union_candidate(method) {
            continue;
        }
        if !seen_va.insert(method.va) {
            continue;
        }
        let mut best: Option<UnionTable> = None;
        for (begin, end) in method_windows(pe, &pdata, method.va) {
            let size = end.saturating_sub(begin) as usize;
            if size < 24 {
                continue;
            }
            let Some(bytes) = pe.slice_va(begin, size) else {
                continue;
            };
            for (max_tag, table_rva) in find_jumptables_in_bytes(bytes) {
                if !jumptable_is_code(pe, table_rva, max_tag) {
                    continue;
                }
                let Some(union) = build_union(
                    pe,
                    meta,
                    ts,
                    max_tag,
                    table_rva,
                    method,
                    wrapper_by_target_td,
                    target_name_by_td,
                    target_name_by_wrapper_td,
                ) else {
                    continue;
                };
                if !union_is_plausible(&union) {
                    continue;
                }
                best = Some(better_union(best, union));
            }
        }
        if let Some(union) = best {
            merge_by_jumptable(&mut by_table, union);
        }
    }
    facts.unions = by_table.into_values().collect();
    facts.unions.sort_by(|a, b| a.static_type.cmp(&b.static_type));
    facts
}

/// Union tag switches live on Beyond `*ForMemoryPack` wrappers. IL2CPP places
/// the large maps on `.cctor`; `Formatter.Deserialize` is often a short thunk.
fn is_union_candidate(method: &crate::codegen::MethodVa) -> bool {
    if method.name != "Deserialize" && method.name != ".cctor" {
        return false;
    }
    method.declaring_name.contains("ForMemoryPack")
}

/// Decoder-facing `{ "TypeName": { "tag": "Concrete" } }`.
///
/// Keys are the union static type, its wrapper, and the leaf name when that
/// leaf is unique among recovered unions. Ambiguous leaves such as `Data`
/// are omitted so a decoder must use the schema field's full type name.
pub fn decoder_union_map(unions: &[UnionTable]) -> BTreeMap<String, BTreeMap<String, String>> {
    let mut alias_count: BTreeMap<String, usize> = BTreeMap::new();
    for u in unions {
        for alias in union_aliases(&u.static_type) {
            *alias_count.entry(alias).or_insert(0) += 1;
        }
    }
    let mut map = BTreeMap::new();
    for u in unions {
        map.insert(u.static_type.clone(), u.tags.clone());
        if !u.wrapper_type.is_empty() && u.wrapper_type != u.static_type {
            map.entry(u.wrapper_type.clone())
                .or_insert_with(|| u.tags.clone());
        }
        for alias in union_aliases(&u.static_type) {
            if alias_count.get(&alias).copied().unwrap_or(0) == 1
                && alias != u.static_type
                && alias != u.wrapper_type
            {
                map.entry(alias).or_insert_with(|| u.tags.clone());
            }
        }
    }
    map
}

fn type_leaf(name: &str) -> &str {
    name.rsplit(['.', '+']).next().unwrap_or(name)
}

fn union_aliases(static_type: &str) -> Vec<String> {
    let mut out = vec![type_leaf(static_type).to_string()];
    if let Some((_, nested)) = static_type.rsplit_once('.') {
        if nested.contains('+') {
            let parts: Vec<&str> = nested.split('+').collect();
            if parts.len() >= 2 {
                out.push(format!("{}+{}", parts[parts.len() - 2], parts[parts.len() - 1]));
            }
        }
    }
    out
}

fn load_pdata(pe: &PeImage) -> Vec<PdataFn> {
    let Some(sec) = pe.section_by_name(".pdata") else {
        return Vec::new();
    };
    let start = sec.raw_ptr as usize;
    let end = (start + sec.raw_size as usize).min(pe.data.len());
    let mut out = Vec::new();
    let mut off = start;
    while off + 12 <= end {
        let begin = u32::from_le_bytes(pe.data[off..off + 4].try_into().unwrap());
        let finish = u32::from_le_bytes(pe.data[off + 4..off + 8].try_into().unwrap());
        if finish > begin {
            out.push(PdataFn {
                begin_rva: begin,
                end_rva: finish,
            });
        }
        off += 12;
    }
    out
}

fn method_windows(pe: &PeImage, pdata: &[PdataFn], va: u64) -> Vec<(u64, u64)> {
    let entry = function_bounds(pe, pdata, va).unwrap_or((va, va.saturating_add(0x100)));
    let mut out = vec![(va, entry.1.max(va + 24))];
    let entry_size = entry.1.saturating_sub(entry.0);
    if entry_size < COLD_ENTRY_MAX {
        let mut tails = Vec::new();
        if let Some(tail) = thunk_target(pe, entry.0, entry.1) {
            tails.push(tail);
        }
        if let Some(tail) = first_far_branch(pe, entry.0, entry.1) {
            tails.push(tail);
        }
        for tail in tails {
            if let Some(tb) = function_bounds(pe, pdata, tail) {
                if tb.0 != entry.0 && !out.iter().any(|&(a, _)| a == tb.0) {
                    out.push(tb);
                }
            }
        }
    }
    out
}

/// `jmp rel32` / `call rel32` only as the first instruction of a short
/// `.pdata` entry (optional `endbr64`). Bytewise `E9` matching is rejected:
/// that opcode appears inside RIP-relative displacements.
fn thunk_target(pe: &PeImage, begin: u64, end: u64) -> Option<u64> {
    let size = end.saturating_sub(begin) as usize;
    let bytes = pe.slice_va(begin, size.min(16))?;
    let mut i = 0usize;
    if bytes.len() >= 4 && bytes[0] == 0xF3 && bytes[1] == 0x0F && bytes[2] == 0x1E && bytes[3] == 0xFA
    {
        i = 4;
    }
    let op = *bytes.get(i)?;
    if op != 0xE9 && op != 0xE8 {
        return None;
    }
    if bytes.len() < i + 5 {
        return None;
    }
    let rel = i32::from_le_bytes(bytes[i + 1..i + 5].try_into().ok()?);
    let target = begin + i as u64 + 5 + rel as i64 as u64;
    if target < begin || target >= end {
        Some(target)
    } else {
        None
    }
}

/// Follow `jcc rel32` that leaves the `.pdata` entry. Unconditional `jmp rel32`
/// (`E9`) is not scanned here: that opcode appears inside RIP-relative
/// displacements (`cmp byte [rip+disp], 0` contains `E9`) and would stitch a
/// 25-byte thunk onto a neighbour's `.cctor`. Leading `E9` thunks are handled
/// by `thunk_target`.
fn first_far_branch(pe: &PeImage, begin: u64, end: u64) -> Option<u64> {
    let size = end.saturating_sub(begin) as usize;
    let bytes = pe.slice_va(begin, size)?;
    let mut i = 0usize;
    while i + 6 <= bytes.len() {
        if bytes[i] == 0x0F && (0x80..=0x8F).contains(&bytes[i + 1]) {
            let rel = i32::from_le_bytes(bytes[i + 2..i + 6].try_into().ok()?);
            let target = begin + i as u64 + 6 + rel as i64 as u64;
            if target < begin || target >= end {
                return Some(target);
            }
        }
        i += 1;
    }
    None
}

fn function_bounds(pe: &PeImage, pdata: &[PdataFn], va: u64) -> Option<(u64, u64)> {
    if va < pe.image_base {
        return None;
    }
    let rva = (va - pe.image_base) as u32;
    let mut lo = 0usize;
    let mut hi = pdata.len();
    while lo < hi {
        let mid = (lo + hi) / 2;
        if pdata[mid].begin_rva <= rva {
            lo = mid + 1;
        } else {
            hi = mid;
        }
    }
    if lo == 0 {
        return None;
    }
    let f = &pdata[lo - 1];
    if rva >= f.begin_rva && rva < f.end_rva {
        return Some((
            pe.image_base + f.begin_rva as u64,
            pe.image_base + f.end_rva as u64,
        ));
    }
    None
}

fn build_union(
    pe: &PeImage,
    meta: &Metadata,
    ts: &mut TypeSystem<'_>,
    max_tag: i32,
    table_rva: u32,
    method: &crate::codegen::MethodVa,
    wrapper_by_target_td: &BTreeMap<usize, String>,
    target_name_by_td: &BTreeMap<usize, String>,
    target_name_by_wrapper_td: &BTreeMap<usize, String>,
) -> Option<UnionTable> {
    if max_tag < 1 || max_tag > 4096 {
        return None;
    }
    let table_va = pe.image_base + table_rva as u64;
    let mut tags = BTreeMap::new();
    let mut evidence = Vec::new();
    let mut skipped = Vec::new();
    for tag in 0..=max_tag {
        let Some(rel) = pe.read_u32_va(table_va + (tag as u64) * 4) else {
            skipped.push(tag);
            continue;
        };
        let mut case_va = pe.image_base + rel as u64;
        case_va = follow_jmps(pe, case_va, 6);
        let Some(type_index) = find_typeinfo_index(pe, case_va) else {
            skipped.push(tag);
            continue;
        };
        let Some(td) = typedef_from_type_index(pe, ts, type_index) else {
            skipped.push(tag);
            continue;
        };
        let type_name = target_name_by_td
            .get(&td)
            .cloned()
            .or_else(|| target_name_by_wrapper_td.get(&td).cloned())
            .unwrap_or_else(|| ts.typedef_full_name(td as i32));
        let wrapper = wrapper_by_target_td.get(&td).cloned().or_else(|| {
            target_name_by_wrapper_td
                .contains_key(&td)
                .then(|| ts.typedef_full_name(td as i32))
        });
        tags.insert(tag.to_string(), type_name.clone());
        evidence.push(UnionTag {
            tag,
            type_name,
            wrapper_type: wrapper,
            typedef_index: td,
            case_va: format!("{case_va:#x}"),
        });
    }
    if tags.len() < 2 {
        return None;
    }
    let (static_type, wrapper_type) =
        union_owner(meta, ts, method.typedef_index, target_name_by_wrapper_td);
    Some(UnionTable {
        static_type,
        wrapper_type,
        formatter_type: method.declaring_name.clone(),
        deserialize_va: format!("{:#x}", method.va),
        max_tag,
        jumptable_rva: format!("{table_rva:#x}"),
        tags,
        evidence,
        skipped,
        status: "native_jumptable".into(),
    })
}

fn union_owner(
    meta: &Metadata,
    ts: &TypeSystem<'_>,
    formatter_td: usize,
    target_by_typedef: &BTreeMap<usize, String>,
) -> (String, String) {
    let td = &meta.type_defs[formatter_td];
    let wrapper_td = ts
        .typedef_index_of(td.declaring_type_index)
        .map(|i| i as usize)
        .unwrap_or(formatter_td);
    let wrapper_name = meta
        .type_name(&meta.type_defs[wrapper_td])
        .unwrap_or_else(|_| "?".into());
    let static_type = target_by_typedef
        .get(&wrapper_td)
        .cloned()
        .unwrap_or(wrapper_name.clone());
    (static_type, wrapper_name)
}

fn union_is_plausible(table: &UnionTable) -> bool {
    let unique = table.tags.values().collect::<HashSet<_>>().len();
    unique >= 2 && unique * 5 >= table.tags.len() * 4
}

fn better_union(best: Option<UnionTable>, candidate: UnionTable) -> UnionTable {
    match best {
        None => candidate,
        Some(cur) => pick_union(cur, candidate),
    }
}

fn merge_by_jumptable(best: &mut BTreeMap<String, UnionTable>, candidate: UnionTable) {
    match best.remove(&candidate.jumptable_rva) {
        None => {
            best.insert(candidate.jumptable_rva.clone(), candidate);
        }
        Some(cur) => {
            let keep = pick_union(cur, candidate);
            best.insert(keep.jumptable_rva.clone(), keep);
        }
    }
}

fn pick_union(cur: UnionTable, candidate: UnionTable) -> UnionTable {
    let cu = cur.tags.values().collect::<HashSet<_>>().len();
    let nu = candidate.tags.values().collect::<HashSet<_>>().len();
    if nu > cu || (nu == cu && candidate.tags.len() > cur.tags.len()) {
        candidate
    } else {
        cur
    }
}

fn jumptable_is_code(pe: &PeImage, table_rva: u32, max_tag: i32) -> bool {
    let n = (max_tag as u64).saturating_add(1);
    if n == 0 {
        return false;
    }
    let table_va = pe.image_base + table_rva as u64;
    let mut exec = 0u64;
    for tag in 0..n {
        let Some(rel) = pe.read_u32_va(table_va + tag * 4) else {
            continue;
        };
        if pe.is_executable_va(pe.image_base + rel as u64) {
            exec += 1;
        }
    }
    exec * 2 >= n
}

fn find_jumptables_in_bytes(bytes: &[u8]) -> Vec<(i32, u32)> {
    let mut out = Vec::new();
    let mut i = 0usize;
    while i + 16 < bytes.len() {
        let Some((imm, inst_len)) = parse_cmp_imm(&bytes[i..]) else {
            i += 1;
            continue;
        };
        if !(1..=4096).contains(&imm) {
            i += 1;
            continue;
        }
        let after = i + inst_len;
        let Some(ja_len) = match_ja_prefix(bytes.get(after..).unwrap_or(&[])) else {
            i += 1;
            continue;
        };
        let load_at = after + ja_len;
        if load_at >= bytes.len() {
            break;
        }
        let window_end = bytes.len().min(load_at + 96);
        let window = &bytes[load_at..window_end];
        for k in 0..window.len() {
            if let Some((rva, load_len)) = parse_scaled_index_load(&window[k..]) {
                if has_index_dispatch(window.get(k + load_len..).unwrap_or(&[])) {
                    out.push((imm, rva));
                    break;
                }
            }
        }
        i += 1;
    }
    out
}

/// `ja` / `jae` rel8 or rel32 immediately after the tag compare.
fn match_ja_prefix(bytes: &[u8]) -> Option<usize> {
    match bytes.first().copied()? {
        0x77 | 0x73 if bytes.len() >= 2 => Some(2),
        0x0F if matches!(bytes.get(1), Some(&0x87 | &0x83)) && bytes.len() >= 6 => Some(6),
        _ => None,
    }
}

/// CMP r/m32, imm (opcodes `3D` / `81 /7` / `83 /7`), optional REX.
fn parse_cmp_imm(bytes: &[u8]) -> Option<(i32, usize)> {
    if bytes.is_empty() {
        return None;
    }
    let mut i = 0usize;
    if bytes[0] & 0xF0 == 0x40 {
        i = 1;
        if bytes.len() < 2 {
            return None;
        }
    }
    match bytes.get(i).copied()? {
        0x3D => {
            if bytes.len() < i + 5 {
                return None;
            }
            let imm = i32::from_le_bytes(bytes[i + 1..i + 5].try_into().ok()?);
            Some((imm, i + 5))
        }
        0x81 | 0x83 => {
            let imm8 = bytes[i] == 0x83;
            let addr = modrm_addr_len(&bytes[i + 1..])?;
            let modrm = *bytes.get(i + 1)?;
            if (modrm >> 3) & 7 != 7 {
                return None;
            }
            let imm_at = i + 1 + addr;
            if imm8 {
                let imm = *bytes.get(imm_at)? as i8 as i32;
                Some((imm, imm_at + 1))
            } else if bytes.len() >= imm_at + 4 {
                let imm = i32::from_le_bytes(bytes[imm_at..imm_at + 4].try_into().ok()?);
                Some((imm, imm_at + 4))
            } else {
                None
            }
        }
        _ => None,
    }
}

fn modrm_addr_len(bytes: &[u8]) -> Option<usize> {
    let modrm = *bytes.first()?;
    let mod_bits = modrm >> 6;
    let rm = modrm & 7;
    if mod_bits == 3 {
        return Some(1);
    }
    let mut n = 1usize;
    let mut sib_base = None;
    if rm == 4 {
        let sib = *bytes.get(1)?;
        sib_base = Some(sib & 7);
        n += 1;
    }
    match mod_bits {
        0 if rm == 5 || sib_base == Some(5) => n += 4,
        1 => n += 1,
        2 => n += 4,
        _ => {}
    }
    if bytes.len() < n {
        return None;
    }
    Some(n)
}

/// `mov r32, [base + index*4 + disp32]` with optional REX.
fn parse_scaled_index_load(bytes: &[u8]) -> Option<(u32, usize)> {
    let mut i = 0usize;
    if bytes.first().copied()? & 0xF0 == 0x40 {
        i = 1;
    }
    if bytes.get(i) != Some(&0x8B) {
        return None;
    }
    let modrm = *bytes.get(i + 1)?;
    if modrm & 0xC0 != 0x80 || modrm & 7 != 4 {
        return None;
    }
    let sib = *bytes.get(i + 2)?;
    if sib >> 6 != 2 {
        return None;
    }
    if bytes.len() < i + 7 {
        return None;
    }
    let rva = u32::from_le_bytes(bytes[i + 3..i + 7].try_into().ok()?);
    Some((rva, i + 7))
}

fn has_index_dispatch(bytes: &[u8]) -> bool {
    let n = bytes.len().min(12);
    let slice = &bytes[..n];
    let has_add = slice.contains(&0x03);
    let has_jmp = slice
        .windows(2)
        .any(|w| w[0] == 0xFF && (0xE0..=0xE7).contains(&w[1]));
    has_add && has_jmp
}

fn follow_jmps(pe: &PeImage, mut va: u64, limit: usize) -> u64 {
    for _ in 0..limit {
        let Some(b0) = pe.read_u8_va(va) else {
            return va;
        };
        if b0 == 0xEB {
            let Some(rel) = pe.read_u8_va(va + 1) else {
                return va;
            };
            va = va.wrapping_add(2).wrapping_add(rel as i8 as i64 as u64);
            continue;
        }
        if b0 == 0xE9 {
            let Some(rel) = pe.read_i32_va(va + 1) else {
                return va;
            };
            va = va.wrapping_add(5).wrapping_add(rel as i64 as u64);
            continue;
        }
        return va;
    }
    va
}

fn find_typeinfo_index(pe: &PeImage, start: u64) -> Option<i32> {
    for off in 0..256u64 {
        let va = start + off;
        let Some(b0) = pe.read_u8_va(va) else {
            break;
        };
        // 48 8B 0D/15/05 disp32  mov r64, [rip+disp]
        if b0 != 0x48 || pe.read_u8_va(va + 1) != Some(0x8B) {
            continue;
        }
        let Some(modrm) = pe.read_u8_va(va + 2) else {
            continue;
        };
        if modrm & 0xC7 != 0x05 {
            continue;
        }
        let Some(rel) = pe.read_i32_va(va + 3) else {
            continue;
        };
        let slot = va.wrapping_add(7).wrapping_add(rel as i64 as u64);
        let Some(enc) = pe.read_u64_va(slot) else {
            continue;
        };
        if enc & 1 == 0 {
            continue;
        }
        let usage = ((enc >> 29) & 7) as u32;
        if usage != 1 {
            continue;
        }
        return Some(((enc >> 1) & 0x0FFF_FFFF) as i32);
    }
    None
}

fn typedef_from_type_index(pe: &PeImage, ts: &TypeSystem<'_>, type_index: i32) -> Option<usize> {
    if type_index < 0 || type_index as usize >= ts.type_ptrs.len() {
        return None;
    }
    let ty_va = ts.type_ptrs[type_index as usize];
    let data = pe.read_u64_va(ty_va)?;
    let td = data as i32;
    if td >= 0 && (td as usize) < ts.meta.type_defs.len() {
        Some(td as usize)
    } else {
        None
    }
}

#[allow(dead_code)]
fn first_member_count_cmp(pe: &PeImage, begin: u64, end: u64) -> Option<i32> {
    let size = end.saturating_sub(begin).min(0x200) as usize;
    let bytes = pe.slice_va(begin, size)?;
    let mut i = 0usize;
    while i + 6 < bytes.len() {
        if let Some((imm, len)) = parse_cmp_imm(&bytes[i..]) {
            if (1..=256).contains(&imm) {
                return Some(imm);
            }
            i += len;
            continue;
        }
        i += 1;
    }
    None
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn cmp_imm_register_and_stack_forms() {
        assert_eq!(
            parse_cmp_imm(&hex("817c243053050000")),
            Some((0x553, 8)),
            "cmp dword [rsp+0x30], 0x553"
        );
        assert_eq!(
            parse_cmp_imm(&hex("81febe010000")),
            Some((0x1BE, 6)),
            "cmp esi, 0x1BE"
        );
        assert_eq!(parse_cmp_imm(&hex("83f814")), Some((0x14, 3)), "cmp eax, 0x14");
        assert_eq!(parse_cmp_imm(&hex("83fe31")), Some((0x31, 3)), "cmp esi, 0x31");
        assert_eq!(parse_cmp_imm(&hex("3def000000")), Some((0xEF, 5)), "cmp eax, 0xEF");
        assert_eq!(
            parse_cmp_imm(&hex("83fe0a7545")),
            Some((0x0A, 3)),
            "cmp esi, 0x0A still parses; ja is required separately"
        );
    }

    #[test]
    fn scaled_index_disp32_forms() {
        assert_eq!(
            parse_scaled_index_load(&hex("8b848108e0a904")),
            Some((0x04A9_E008, 7))
        );
        assert_eq!(
            parse_scaled_index_load(&hex("8b8cb2c8a4cc05")),
            Some((0x05CC_A4C8, 7))
        );
        assert_eq!(
            parse_scaled_index_load(&hex("418b8cb780270606")),
            Some((0x0606_2780, 8))
        );
    }

    #[test]
    fn action_base_rip_relative_switch() {
        // cmp [rsp+0x30], 0x553; ja; movsxd; lea; mov eax, [rcx+rax*4+rva]; add; jmp
        let bytes = hex(
            "817c2430530500000f87603500004863442430488d0dce185cfb8b848108e0a9044803c1ffe0",
        );
        assert_eq!(find_jumptables_in_bytes(&bytes), vec![(0x553, 0x04A9_E008)]);
    }

    #[test]
    fn ability_action_esi_switch() {
        let bytes = hex(
            "81febe0100000f8783860000488d150ce233fa8b8cb2c8a4cc054803caffe1",
        );
        assert_eq!(find_jumptables_in_bytes(&bytes), vec![(0x1BE, 0x05CC_A4C8)]);
    }

    #[test]
    fn condition_runtime_r15_switch() {
        let bytes = hex("83fe310f870a0b0000418b8cb7802706064903cfffe1");
        assert_eq!(find_jumptables_in_bytes(&bytes), vec![(0x31, 0x0606_2780)]);
    }

    #[test]
    fn jae_rel32_switch() {
        let bytes = hex("83fe050f830a0000008b8cb2c8a4cc054803caffe1");
        assert_eq!(find_jumptables_in_bytes(&bytes), vec![(5, 0x05CC_A4C8)]);
    }

    #[test]
    fn jne_is_not_a_union_switch() {
        let bytes = hex("83fe0a7545488b0f");
        assert!(find_jumptables_in_bytes(&bytes).is_empty());
    }

    #[test]
    fn decoder_map_omits_ambiguous_data_leaf() {
        let unions = vec![
            UnionTable {
                static_type: "Beyond.Gameplay.Core.AbilityActionData".into(),
                wrapper_type: "AbilityActionDataForMemoryPack".into(),
                formatter_type: "Fmt".into(),
                deserialize_va: "0x1".into(),
                max_tag: 1,
                jumptable_rva: "0x2".into(),
                tags: BTreeMap::from([
                    ("0".into(), "A".into()),
                    ("1".into(), "B".into()),
                ]),
                evidence: vec![],
                skipped: vec![],
                status: "native_jumptable".into(),
            },
            UnionTable {
                static_type: "Beyond.Gameplay.Core.Selector+Finder+Data".into(),
                wrapper_type: "FinderDataForMemoryPack".into(),
                formatter_type: "Fmt2".into(),
                deserialize_va: "0x3".into(),
                max_tag: 1,
                jumptable_rva: "0x4".into(),
                tags: BTreeMap::from([
                    ("0".into(), "HitBoxFinder".into()),
                    ("1".into(), "OtherFinder".into()),
                ]),
                evidence: vec![],
                skipped: vec![],
                status: "native_jumptable".into(),
            },
            UnionTable {
                static_type: "Beyond.Gameplay.Core.Selector+Validator+Data".into(),
                wrapper_type: "ValidatorDataForMemoryPack".into(),
                formatter_type: "Fmt3".into(),
                deserialize_va: "0x5".into(),
                max_tag: 1,
                jumptable_rva: "0x6".into(),
                tags: BTreeMap::from([
                    ("0".into(), "V0".into()),
                    ("1".into(), "V1".into()),
                ]),
                evidence: vec![],
                skipped: vec![],
                status: "native_jumptable".into(),
            },
        ];
        let map = decoder_union_map(&unions);
        assert!(map.contains_key("Beyond.Gameplay.Core.AbilityActionData"));
        assert!(map.contains_key("AbilityActionData"));
        assert!(map.contains_key("Beyond.Gameplay.Core.Selector+Finder+Data"));
        assert!(map.contains_key("Finder+Data"));
        assert!(map.contains_key("Validator+Data"));
        assert!(!map.contains_key("Data"));
        assert_eq!(map["AbilityActionData"]["0"], "A");
        assert_eq!(
            map["Beyond.Gameplay.Core.Selector+Finder+Data"]["0"],
            "HitBoxFinder"
        );
    }

    fn hex(s: &str) -> Vec<u8> {
        let s: String = s.chars().filter(|c| !c.is_whitespace()).collect();
        (0..s.len())
            .step_by(2)
            .map(|i| u8::from_str_radix(&s[i..i + 2], 16).unwrap())
            .collect()
    }
}
