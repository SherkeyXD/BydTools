//! Enum underlying widths and unmanaged/blittable layouts from metadata.
//!
//! A custom Formatter may still override the wire width. Those overrides are
//! recorded separately from native analysis; this module only emits what the
//! TypeDef / field layout actually stores.

use crate::metadata::{Metadata, TypeDefinition};
use crate::pe::PeImage;
use crate::reg::MetadataRegistration;
use crate::typesys::{field_attrs, TypeSystem};
use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, HashMap, HashSet};

pub const VALUETYPE_BIT: u32 = 1 << 0;
pub const ENUMTYPE_BIT: u32 = 1 << 1;
pub const BLITTABLE_BIT: u32 = 1 << 4;

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct EnumLayout {
    pub type_name: String,
    pub typedef_index: usize,
    pub underlying: String,
    pub width: u8,
    pub status: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct UnmanagedField {
    pub name: String,
    #[serde(rename = "type")]
    pub ty: String,
    pub offset: i32,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub size: Option<i32>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct UnmanagedLayout {
    pub type_name: String,
    pub typedef_index: usize,
    pub instance_size: i32,
    pub native_size: i32,
    pub payload_size: i32,
    pub blittable: bool,
    pub fields: Vec<UnmanagedField>,
    pub status: String,
}

#[derive(Debug, Clone, Copy, Default)]
pub struct TypeSize {
    pub instance_size: i32,
    pub native_size: i32,
}

#[derive(Debug, Clone, Default)]
pub struct TypeLayouts {
    pub instance_size: Vec<i32>,
    pub enums: BTreeMap<String, EnumLayout>,
    pub unmanaged: BTreeMap<String, UnmanagedLayout>,
}

impl TypeLayouts {
    pub fn enum_width(&self, type_name: &str) -> Option<u8> {
        self.enums.get(type_name).map(|e| e.width)
    }
}

pub fn is_valuetype(td: &TypeDefinition) -> bool {
    td.bitfield & VALUETYPE_BIT != 0
}

pub fn is_enumtype(td: &TypeDefinition) -> bool {
    td.bitfield & ENUMTYPE_BIT != 0
}

pub fn is_blittable(td: &TypeDefinition) -> bool {
    td.bitfield & BLITTABLE_BIT != 0
}

/// `Il2CppTypeDefinitionSizes**` — pointer table, same shape as `fieldOffsets`.
pub fn load_type_sizes(pe: &PeImage, reg: &MetadataRegistration) -> Vec<TypeSize> {
    let n = reg.type_definitions_sizes_count as usize;
    let mut out = vec![TypeSize::default(); n];
    if reg.type_definitions_sizes == 0 {
        return out;
    }
    if let Some(ptrs) = pe.read_u64_array_va(reg.type_definitions_sizes, n) {
        let mut hits = 0usize;
        for (i, ptr) in ptrs.into_iter().enumerate() {
            if ptr == 0 || !pe.va_mapped(ptr) {
                continue;
            }
            let Some(instance_size) = pe.read_i32_va(ptr) else {
                continue;
            };
            let native_size = pe.read_i32_va(ptr + 4).unwrap_or(-1);
            out[i] = TypeSize {
                instance_size,
                native_size,
            };
            if instance_size > 0 {
                hits += 1;
            }
        }
        if hits > 0 {
            return out;
        }
    }
    // Packed 16-byte structs (synthetic images / unexpected IL2CPP).
    for (i, slot) in out.iter_mut().enumerate() {
        let Some(sz) = pe.read_i32_va(reg.type_definitions_sizes + (i as u64) * 16) else {
            break;
        };
        slot.instance_size = sz;
        slot.native_size = pe
            .read_i32_va(reg.type_definitions_sizes + (i as u64) * 16 + 4)
            .unwrap_or(-1);
    }
    out
}

pub fn load_instance_sizes(pe: &PeImage, reg: &MetadataRegistration) -> Vec<i32> {
    load_type_sizes(pe, reg)
        .into_iter()
        .map(|s| s.instance_size)
        .collect()
}

pub fn collect_layouts(
    meta: &Metadata,
    ts: &mut TypeSystem<'_>,
    field_offs: &HashMap<(usize, usize), i32>,
    type_sizes: &[TypeSize],
    referenced: &HashSet<String>,
) -> TypeLayouts {
    let mut enums = BTreeMap::new();
    for (ti, td) in meta.type_defs.iter().enumerate() {
        if !is_enumtype(td) {
            continue;
        }
        let name = ts.typedef_full_name(ti as i32);
        if let Some(layout) = enum_layout(meta, ts, ti, td, &name) {
            enums.insert(name, layout);
        }
    }

    let mut unmanaged = BTreeMap::new();
    let mut memcpy_ok = vec![false; meta.type_defs.len()];
    for (ti, td) in meta.type_defs.iter().enumerate() {
        memcpy_ok[ti] = is_enumtype(td) || valuetype_is_memcpy(meta, ts, ti, td);
    }
    // Nested valuetypes (AnimationClipAsyncInfo.averageSpeed: Vector3).
    for _ in 0..8 {
        let mut changed = false;
        for (ti, td) in meta.type_defs.iter().enumerate() {
            if memcpy_ok[ti] || !is_valuetype(td) || is_enumtype(td) {
                continue;
            }
            if valuetype_fields_are_memcpy(meta, ts, ti, td, &memcpy_ok) {
                memcpy_ok[ti] = true;
                changed = true;
            }
        }
        if !changed {
            break;
        }
    }

    for (ti, td) in meta.type_defs.iter().enumerate() {
        let name = ts.typedef_full_name(ti as i32);
        if is_enumtype(td) || !is_valuetype(td) {
            continue;
        }
        if !referenced.contains(&name) && !is_unity_packed(&name) {
            continue;
        }
        // native_size > 0 is not enough: valuetypes that hold a string/object
        // pointer still have a native size (the pointer width). Only emit
        // types whose instance fields are primitives, enums, or nested memcpy.
        if !memcpy_ok[ti] && !is_unity_packed(&name) {
            continue;
        }
        if let Some(layout) = unmanaged_layout(
            meta,
            ts,
            field_offs,
            type_sizes,
            &enums,
            &memcpy_ok,
            ti,
            td,
            &name,
        ) {
            unmanaged.insert(name, layout);
        }
    }

    TypeLayouts {
        instance_size: type_sizes.iter().map(|s| s.instance_size).collect(),
        enums,
        unmanaged,
    }
}

fn enum_layout(
    meta: &Metadata,
    ts: &mut TypeSystem<'_>,
    ti: usize,
    td: &TypeDefinition,
    name: &str,
) -> Option<EnumLayout> {
    if td.field_count == 0 || td.field_index < 0 {
        return None;
    }
    let start = td.field_index as usize;
    for fi in start..start + td.field_count as usize {
        let f = meta.fields.get(fi)?;
        let fname = meta.string(f.name_index).ok()?;
        if fname != "value__" {
            continue;
        }
        let underlying = ts.name_of_index(f.type_index);
        let width = primitive_width(&underlying)?;
        return Some(EnumLayout {
            type_name: name.to_string(),
            typedef_index: ti,
            underlying,
            width,
            status: "metadata_value__".into(),
        });
    }
    None
}

fn valuetype_is_memcpy(
    meta: &Metadata,
    ts: &mut TypeSystem<'_>,
    ti: usize,
    td: &TypeDefinition,
) -> bool {
    valuetype_fields_are_memcpy(meta, ts, ti, td, &[])
}

fn valuetype_fields_are_memcpy(
    meta: &Metadata,
    ts: &mut TypeSystem<'_>,
    ti: usize,
    td: &TypeDefinition,
    nested_ok: &[bool],
) -> bool {
    if !is_valuetype(td) || is_enumtype(td) {
        return false;
    }
    let mut n = 0;
    for field in instance_fields(meta, ts, ti, td, &HashMap::new()) {
        if primitive_width(&field.ty).is_some() || is_unity_packed(&field.ty) {
            n += 1;
            continue;
        }
        let Some(field_tdi) = ts.typedef_index_of(field.type_index) else {
            return false;
        };
        let Some(field_td) = meta.type_defs.get(field_tdi as usize) else {
            return false;
        };
        if is_enumtype(field_td) {
            n += 1;
            continue;
        }
        let idx = field_tdi as usize;
        if idx < nested_ok.len() && nested_ok[idx] {
            n += 1;
            continue;
        }
        return false;
    }
    n > 0
}

struct InstanceField {
    name: String,
    ty: String,
    type_index: i32,
    offset: i32,
}

fn instance_fields(
    meta: &Metadata,
    ts: &mut TypeSystem<'_>,
    ti: usize,
    td: &TypeDefinition,
    field_offs: &HashMap<(usize, usize), i32>,
) -> Vec<InstanceField> {
    let mut out = Vec::new();
    if td.field_count == 0 || td.field_index < 0 {
        return out;
    }
    let start = td.field_index as usize;
    for (local, fi) in (start..start + td.field_count as usize).enumerate() {
        let Some(f) = meta.fields.get(fi) else {
            break;
        };
        let attrs = ts.attrs_of_index(f.type_index);
        if attrs & field_attrs::STATIC != 0 || attrs & field_attrs::LITERAL != 0 {
            continue;
        }
        out.push(InstanceField {
            name: meta.string(f.name_index).unwrap_or_else(|_| "?".into()),
            ty: ts.name_of_index(f.type_index),
            type_index: f.type_index,
            offset: field_offs.get(&(ti, local)).copied().unwrap_or(0),
        });
    }
    out
}

fn field_payload_size(
    meta: &Metadata,
    ts: &mut TypeSystem<'_>,
    enums: &BTreeMap<String, EnumLayout>,
    type_sizes: &[TypeSize],
    field: &InstanceField,
) -> Option<i32> {
    if let Some(w) = primitive_width(&field.ty) {
        return Some(w as i32);
    }
    if let Some(n) = unity_packed_size(&field.ty) {
        return Some(n);
    }
    if let Some(e) = enums.get(&field.ty) {
        return Some(e.width as i32);
    }
    let tdi = ts.typedef_index_of(field.type_index)? as usize;
    if meta.type_defs.get(tdi).is_some_and(is_enumtype) {
        if let Some(e) = enums.values().find(|e| e.typedef_index == tdi) {
            return Some(e.width as i32);
        }
    }
    let native = type_sizes.get(tdi).map(|s| s.native_size).unwrap_or(-1);
    if native > 0 {
        return Some(native);
    }
    None
}

fn unmanaged_layout(
    meta: &Metadata,
    ts: &mut TypeSystem<'_>,
    field_offs: &HashMap<(usize, usize), i32>,
    type_sizes: &[TypeSize],
    enums: &BTreeMap<String, EnumLayout>,
    _memcpy_ok: &[bool],
    ti: usize,
    td: &TypeDefinition,
    name: &str,
) -> Option<UnmanagedLayout> {
    let sizes = type_sizes.get(ti).copied().unwrap_or_default();
    let raw = instance_fields(meta, ts, ti, td, field_offs);
    let mut fields = Vec::new();
    for field in &raw {
        fields.push(UnmanagedField {
            size: field_payload_size(meta, ts, enums, type_sizes, field),
            name: field.name.clone(),
            ty: field.ty.clone(),
            offset: field.offset,
        });
    }
    if fields.is_empty() && sizes.native_size <= 0 {
        return None;
    }
    let min_off = fields
        .iter()
        .map(|f| f.offset)
        .min()
        .unwrap_or(0);
    let boxed = min_off >= 16;
    if boxed {
        for f in &mut fields {
            f.offset -= 16;
        }
    }
    let mut end = 0i32;
    for f in &fields {
        let sz = f.size.unwrap_or(0);
        end = end.max(f.offset + sz);
    }
    let mut payload = align4(end);
    if payload <= 0 && sizes.native_size > 0 {
        payload = sizes.native_size;
    }
    if payload <= 0 {
        return None;
    }
    Some(UnmanagedLayout {
        type_name: name.to_string(),
        typedef_index: ti,
        instance_size: sizes.instance_size,
        native_size: sizes.native_size,
        payload_size: payload,
        blittable: is_blittable(td),
        fields,
        status: "il2cpp_native_valuetype".into(),
    })
}

fn align4(n: i32) -> i32 {
    if n <= 0 {
        0
    } else {
        (n + 3) & !3
    }
}

fn unity_packed_size(name: &str) -> Option<i32> {
    match name {
        "UnityEngine.Vector2" | "UnityEngine.Vector2Int" => Some(8),
        "UnityEngine.Vector3" | "UnityEngine.Vector3Int" => Some(12),
        "UnityEngine.Vector4" | "UnityEngine.Quaternion" | "UnityEngine.Color" => Some(16),
        "UnityEngine.Color32" => Some(4),
        _ => None,
    }
}

pub fn primitive_width(ty: &str) -> Option<u8> {
    match ty {
        "bool" | "System.Boolean" | "sbyte" | "System.SByte" | "byte" | "System.Byte" => Some(1),
        "short" | "System.Int16" | "ushort" | "System.UInt16" | "char" | "System.Char" => Some(2),
        "int" | "System.Int32" | "uint" | "System.UInt32" | "float" | "System.Single" => Some(4),
        "long" | "System.Int64" | "ulong" | "System.UInt64" | "double" | "System.Double" => Some(8),
        _ => None,
    }
}

fn is_unity_packed(name: &str) -> bool {
    matches!(
        name,
        "UnityEngine.Vector2"
            | "UnityEngine.Vector3"
            | "UnityEngine.Vector4"
            | "UnityEngine.Quaternion"
            | "UnityEngine.Color"
            | "UnityEngine.Color32"
            | "UnityEngine.Vector2Int"
            | "UnityEngine.Vector3Int"
    )
}

pub fn skip_parent_name(name: &str) -> bool {
    matches!(
        name,
        "object"
            | "System.Object"
            | "ValueType"
            | "System.ValueType"
            | "Enum"
            | "System.Enum"
            | "System.MulticastDelegate"
            | "System.Delegate"
    )
}
