//! Beyond.MemoryPack schema export.
//!
//! Endfield does not use classic `[MemoryPackable]` CustomAttributes. Schema
//! comes from `*ForMemoryPack` wrapper types. Canonical wire order is the
//! wrapper `__name__` property metadata order, which matches native
//! `Deserialize` setter order — not the target type's field declaration order.

use crate::codegen;
use crate::layout::{self, EnumLayout, TypeLayouts, UnmanagedLayout};
use crate::metadata::{Metadata, TypeDefinition};
use crate::native::{self, UnionTable};
use crate::pe::PeImage;
use crate::protocol::{self, ProtocolDocument};
use crate::reg::{load_field_offsets, MetadataRegistration};
use crate::type_cs::render_type_cs;
use crate::type_index::build_type_index;
use crate::typesys::{field_attrs, TypeEnum, TypeSystem};
use anyhow::{Context, Result};
use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, HashMap, HashSet};
use std::fs::{self, File};
use std::io::Write;
use std::path::{Path, PathBuf};

pub const WIRE_ORDER_SOURCE: &str =
    "wrapper __name__ property metadata order (confirmed = native Deserialize call order)";

pub const ORDER_NOTE: &str = "wire_order / members_from_wrapper_props is the canonical binary \
member order (GameAssembly Deserialize matches these setters, often alphabetical). \
members_from_target_fields is declaration/layout order only — NOT wire order.";

pub const TARGET_FIELD_NOTE: &str =
    "reference only — instance field declaration / offset layout; NOT MemoryPack wire order";

pub const PURPOSE: &str = "Beyond.MemoryPack offline schema for packed config decryption";

pub const CUSTOM_ATTRIBUTES_NOTE: &str =
    "not used — Endfield AttributeDataRange is MethodDef index lists, not classic CA blobs";

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct MemberProp {
    pub name: String,
    pub raw_property: String,
    #[serde(rename = "type")]
    pub ty: String,
    pub property_index: usize,
    pub has_getter: bool,
    pub has_setter: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct MemberField {
    pub name: String,
    #[serde(rename = "type")]
    pub ty: String,
    pub field_index: usize,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub offset: Option<i32>,
    pub is_public: bool,
    pub note: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct WrapperSchema {
    pub wrapper_type: String,
    pub typedef_index: usize,
    pub assembly: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub target_type: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub target_typedef_index: Option<usize>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub target_assembly: Option<String>,
    pub interfaces: Vec<String>,
    /// Canonical wire / binary member order (= wrapper `__prop__` metadata order).
    pub wire_order: Vec<MemberProp>,
    /// Alias of `wire_order`.
    pub members_from_wrapper_props: Vec<MemberProp>,
    /// Target instance fields in declaration order. Not wire order.
    pub members_from_target_fields: Vec<MemberField>,
    pub orders_differ: bool,
    pub has_formatter: bool,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub formatter_typedef: Option<usize>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub formatter_type: Option<String>,
    pub has_deserialize: bool,
    pub has_register_formatter: bool,
    pub wire_order_source: String,
    pub order_note: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub parent_type: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub parent_typedef_index: Option<usize>,
    #[serde(default, skip_serializing_if = "is_false")]
    pub wire_includes_base: bool,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub native_member_count: Option<i32>,
}

fn is_false(v: &bool) -> bool {
    !*v
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct MarkerSchema {
    pub type_name: String,
    pub typedef_index: usize,
    pub assembly: String,
    pub interfaces: Vec<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub matching_wrapper: Option<String>,
    pub instance_field_count: usize,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Counts {
    pub wrappers: usize,
    pub serialize_markers: usize,
    pub wrappers_orders_differ_from_target_fields: usize,
    pub wrappers_with_formatter: usize,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct RelatedExports {
    pub type_index: String,
    #[serde(default)]
    pub type_cs: String,
    pub readme: String,
    #[serde(default, skip_serializing_if = "String::is_empty")]
    pub protocol: String,
    #[serde(default, skip_serializing_if = "String::is_empty")]
    pub enums: String,
    #[serde(default, skip_serializing_if = "String::is_empty")]
    pub unmanaged: String,
    #[serde(default, skip_serializing_if = "String::is_empty")]
    pub unions: String,
    #[serde(default, skip_serializing_if = "String::is_empty")]
    pub union_map: String,
    pub note: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SchemaDocument {
    pub generated_by: String,
    pub purpose: String,
    pub wire_order_rule: String,
    pub order_caveat: String,
    pub custom_attributes: String,
    pub related_exports: RelatedExports,
    pub counts: Counts,
    pub wrappers: Vec<WrapperSchema>,
    pub serialize_markers: Vec<MarkerSchema>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub protocol_ops: Vec<protocol::ProtocolOp>,
}

#[derive(Debug)]
pub struct ExportBundle {
    pub schemas: SchemaDocument,
    pub type_index: serde_json::Value,
    pub type_cs: String,
    pub readme: String,
    pub protocol: ProtocolDocument,
    pub enums: Vec<EnumLayout>,
    pub unmanaged: Vec<UnmanagedLayout>,
    pub unions: Vec<UnionTable>,
    pub union_map: BTreeMap<String, BTreeMap<String, String>>,
    pub missing: serde_json::Value,
}

pub fn export_bundle(
    meta: &Metadata,
    pe: &PeImage,
    reg: &MetadataRegistration,
    reg_source: &str,
) -> Result<ExportBundle> {
    let protocol = protocol::build_protocol(pe);
    let (mut schemas, layouts, unions) = build_schema_document(meta, pe, reg)?;
    schemas.protocol_ops = protocol.ops.clone();
    let type_index = build_type_index(
        &schemas.wrappers,
        &schemas.serialize_markers,
        &layouts,
    );
    let type_cs = render_type_cs(&schemas);
    let union_map = native::decoder_union_map(&unions);
    let readme = output_readme(&schemas.counts, &unions, reg_source);
    let missing = collect_missing(&schemas.wrappers, &layouts, &unions);
    Ok(ExportBundle {
        schemas,
        type_index,
        type_cs,
        readme,
        protocol,
        enums: layouts.enums.values().cloned().collect(),
        unmanaged: layouts.unmanaged.values().cloned().collect(),
        unions,
        union_map,
        missing,
    })
}

pub fn write_bundle(bundle: &ExportBundle, out_dir: &Path) -> Result<Vec<PathBuf>> {
    fs::create_dir_all(out_dir).with_context(|| format!("create {}", out_dir.display()))?;
    let schemas_path = out_dir.join("schemas.json");
    let index_path = out_dir.join("type_index.json");
    let cs_path = out_dir.join("type.cs");
    let readme_path = out_dir.join("README.md");
    let protocol_path = out_dir.join("protocol.json");
    let enums_path = out_dir.join("enums.json");
    let unmanaged_path = out_dir.join("unmanaged.json");
    let unions_path = out_dir.join("unions.json");
    let union_map_path = out_dir.join("union-map.json");
    let missing_path = out_dir.join("missing.json");
    write_json(&schemas_path, &bundle.schemas)?;
    write_json(&index_path, &bundle.type_index)?;
    write_json(&protocol_path, &bundle.protocol)?;
    write_json(&enums_path, &bundle.enums)?;
    write_json(&unmanaged_path, &bundle.unmanaged)?;
    write_json(&unions_path, &bundle.unions)?;
    write_json(&union_map_path, &bundle.union_map)?;
    write_json(&missing_path, &bundle.missing)?;
    fs::write(&cs_path, &bundle.type_cs).with_context(|| format!("write {}", cs_path.display()))?;
    let mut f =
        File::create(&readme_path).with_context(|| format!("create {}", readme_path.display()))?;
    f.write_all(bundle.readme.as_bytes())?;
    Ok(vec![
        schemas_path,
        index_path,
        protocol_path,
        enums_path,
        unmanaged_path,
        unions_path,
        union_map_path,
        missing_path,
        cs_path,
        readme_path,
    ])
}

pub fn write_json<T: Serialize>(path: &Path, value: &T) -> Result<()> {
    let mut f = File::create(path).with_context(|| format!("create {}", path.display()))?;
    serde_json::to_writer_pretty(&mut f, value)
        .with_context(|| format!("write {}", path.display()))?;
    f.write_all(b"\n")?;
    Ok(())
}

pub fn build_schema_document(
    meta: &Metadata,
    pe: &PeImage,
    reg: &MetadataRegistration,
) -> Result<(SchemaDocument, TypeLayouts, Vec<UnionTable>)> {
    let mut ts = TypeSystem::load(pe, meta, reg.types, reg.types_count)?;
    let field_counts: Vec<u16> = meta.type_defs.iter().map(|td| td.field_count).collect();
    let field_offs = load_field_offsets(pe, &field_counts, reg);
    let type_sizes = layout::load_type_sizes(pe, reg);
    let instance_size: Vec<i32> = type_sizes.iter().map(|s| s.instance_size).collect();
    let td_image = build_td_image_map(meta);

    let mut wrappers = Vec::new();
    let mut wrapper_by_target: HashMap<usize, String> = HashMap::new();
    let mut target_name_by_td: BTreeMap<usize, String> = BTreeMap::new();
    let mut target_name_by_wrapper_td: BTreeMap<usize, String> = BTreeMap::new();

    for (ti, td) in meta.type_defs.iter().enumerate() {
        let name = meta.string(td.name_index).unwrap_or_else(|_| "?".into());
        if !name.ends_with("ForMemoryPack") || name.contains("Formatter") {
            continue;
        }
        let full = meta.type_name(td).unwrap_or_else(|_| name.clone());
        let ifaces: Vec<String> = meta
            .interfaces_of(td)
            .into_iter()
            .map(|ix| ts.name_of_index(ix))
            .collect();
        let schema = build_wrapper_schema(
            meta,
            &mut ts,
            &field_offs,
            &instance_size,
            &td_image,
            ti,
            td,
            &full,
            &ifaces,
        );
        if let Some(tidx) = schema.target_typedef_index {
            wrapper_by_target.insert(tidx, schema.wrapper_type.clone());
            if let Some(tname) = schema.target_type.clone() {
                target_name_by_td.insert(tidx, tname.clone());
                target_name_by_wrapper_td.insert(ti, tname);
            }
        }
        wrappers.push(schema);
    }

    apply_inherited_wire(&mut wrappers, &ts, &instance_size);

    let mut referenced = HashSet::new();
    for w in &wrappers {
        for m in &w.wire_order {
            collect_type_names(&m.ty, &mut referenced);
        }
        if let Some(t) = &w.target_type {
            referenced.insert(t.clone());
        }
    }
    let layouts = layout::collect_layouts(meta, &mut ts, &field_offs, &type_sizes, &referenced);

    let code = codegen::load_code_map(pe, meta).unwrap_or_default();
    let native = native::analyze(
        pe,
        meta,
        &mut ts,
        &code,
        &wrapper_by_target.iter().map(|(k, v)| (*k, v.clone())).collect(),
        &target_name_by_td,
        &target_name_by_wrapper_td,
    );
    apply_native_member_counts(&mut wrappers, &native.member_counts);
    let unions = native.unions;

    let mut markers = Vec::new();
    for (ti, td) in meta.type_defs.iter().enumerate() {
        let name = meta.string(td.name_index).unwrap_or_else(|_| "?".into());
        if name.ends_with("ForMemoryPack") {
            continue;
        }
        let ifaces: Vec<String> = meta
            .interfaces_of(td)
            .into_iter()
            .map(|ix| ts.name_of_index(ix))
            .collect();
        if !ifaces.iter().any(|i| is_serialize_marker_iface(i)) {
            continue;
        }
        let full = ts.typedef_full_name(ti as i32);
        markers.push(MarkerSchema {
            type_name: full,
            typedef_index: ti,
            assembly: td_image.get(ti).cloned().unwrap_or_default(),
            interfaces: ifaces,
            matching_wrapper: wrapper_by_target.get(&ti).cloned(),
            instance_field_count: count_instance_fields(meta, &ts, td),
        });
    }

    wrappers.sort_by(|a, b| a.wrapper_type.cmp(&b.wrapper_type));
    markers.sort_by(|a, b| a.type_name.cmp(&b.type_name));

    let counts = Counts {
        wrappers: wrappers.len(),
        serialize_markers: markers.len(),
        wrappers_orders_differ_from_target_fields: wrappers
            .iter()
            .filter(|w| w.orders_differ)
            .count(),
        wrappers_with_formatter: wrappers.iter().filter(|w| w.has_formatter).count(),
    };

    Ok((
        SchemaDocument {
            generated_by: "beyond-mp-schema".into(),
            purpose: PURPOSE.into(),
            wire_order_rule: WIRE_ORDER_SOURCE.into(),
            order_caveat: ORDER_NOTE.into(),
            custom_attributes: CUSTOM_ATTRIBUTES_NOTE.into(),
            related_exports: RelatedExports {
                type_index: "type_index.json".into(),
                type_cs: "type.cs".into(),
                readme: "README.md".into(),
                protocol: "protocol.json".into(),
                enums: "enums.json".into(),
                unmanaged: "unmanaged.json".into(),
                unions: "unions.json".into(),
                union_map: "union-map.json".into(),
                note: "protocol.json is the read contract. schemas.json wire_order is the binary member order including inherited and underscore members. enums.json / unmanaged.json / unions.json / union-map.json are required to decode without type-name branches. missing.json lists referenced types with no declared rule.".into(),
            },
            counts,
            wrappers,
            serialize_markers: markers,
            protocol_ops: Vec::new(),
        },
        layouts,
        unions,
    ))
}

#[allow(clippy::too_many_arguments)]
fn build_wrapper_schema(
    meta: &Metadata,
    ts: &mut TypeSystem<'_>,
    field_offs: &HashMap<(usize, usize), i32>,
    _instance_size: &[i32],
    td_image: &[String],
    ti: usize,
    td: &TypeDefinition,
    full: &str,
    ifaces: &[String],
) -> WrapperSchema {
    let assembly = td_image.get(ti).cloned().unwrap_or_default();
    let (target_type, target_typedef_index) = resolve_wrapper_target(meta, ts, td, ifaces);
    let target_assembly = target_typedef_index.and_then(|i| td_image.get(i).cloned());
    let (parent_type, parent_typedef_index) = target_typedef_index
        .and_then(|tidx| parent_info(ts, tidx))
        .unwrap_or((None, None));

    let props = collect_wrapper_props(meta, ts, td);
    let fields = if let Some(tidx) = target_typedef_index {
        collect_target_fields(meta, ts, field_offs, tidx)
    } else {
        Vec::new()
    };
    let orders_differ = orders_differ(&props, &fields);
    let (has_formatter, formatter_typedef, formatter_type) = find_nested_formatter(meta, td);
    let (has_deserialize, has_register_formatter) = method_flags(meta, td);

    WrapperSchema {
        wrapper_type: full.to_string(),
        typedef_index: ti,
        assembly,
        target_type,
        target_typedef_index,
        target_assembly,
        interfaces: ifaces.to_vec(),
        members_from_wrapper_props: props.clone(),
        wire_order: props,
        members_from_target_fields: fields,
        orders_differ,
        has_formatter,
        formatter_typedef,
        formatter_type,
        has_deserialize,
        has_register_formatter,
        wire_order_source: WIRE_ORDER_SOURCE.into(),
        order_note: ORDER_NOTE.into(),
        parent_type,
        parent_typedef_index,
        wire_includes_base: false,
        native_member_count: None,
    }
}

fn collect_wrapper_props(
    meta: &Metadata,
    ts: &mut TypeSystem<'_>,
    td: &TypeDefinition,
) -> Vec<MemberProp> {
    let mut out = Vec::new();
    if td.property_count == 0 || td.property_index < 0 {
        return out;
    }
    let start = td.property_index as usize;
    for (local, pi) in (start..start + td.property_count as usize).enumerate() {
        let Some(p) = meta.properties.get(pi) else {
            break;
        };
        let raw = meta.string(p.name_index).unwrap_or_else(|_| "?".into());
        let Some(stripped) = strip_dunder(&raw) else {
            continue;
        };
        if stripped == "instance" {
            continue;
        }
        let ty = property_type_name(meta, ts, td, p.get, p.set);
        out.push(MemberProp {
            name: stripped,
            raw_property: raw,
            ty,
            property_index: local,
            has_getter: p.get >= 0,
            has_setter: p.set >= 0,
        });
    }
    out
}

fn collect_target_fields(
    meta: &Metadata,
    ts: &mut TypeSystem<'_>,
    field_offs: &HashMap<(usize, usize), i32>,
    tidx: usize,
) -> Vec<MemberField> {
    let mut out = Vec::new();
    let Some(td) = meta.type_defs.get(tidx) else {
        return out;
    };
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
        let name = meta.string(f.name_index).unwrap_or_else(|_| "?".into());
        let ty = ts.name_of_index(f.type_index);
        let is_public = (attrs & field_attrs::FIELD_ACCESS_MASK) == field_attrs::PUBLIC;
        out.push(MemberField {
            name,
            ty,
            field_index: local,
            offset: field_offs.get(&(tidx, local)).copied(),
            is_public,
            note: TARGET_FIELD_NOTE.into(),
        });
    }
    out
}

fn count_instance_fields(meta: &Metadata, ts: &TypeSystem<'_>, td: &TypeDefinition) -> usize {
    if td.field_count == 0 || td.field_index < 0 {
        return 0;
    }
    let start = td.field_index as usize;
    let mut n = 0;
    for fi in start..start + td.field_count as usize {
        let Some(f) = meta.fields.get(fi) else {
            break;
        };
        let attrs = ts.attrs_of_index(f.type_index);
        if attrs & field_attrs::STATIC == 0 && attrs & field_attrs::LITERAL == 0 {
            n += 1;
        }
    }
    n
}

pub fn orders_differ(props: &[MemberProp], fields: &[MemberField]) -> bool {
    if props.is_empty() || fields.is_empty() {
        return false;
    }
    let pn: Vec<&str> = props.iter().map(|m| m.name.as_str()).collect();
    let fnames: Vec<&str> = fields.iter().map(|m| m.name.as_str()).collect();
    if pn.len() != fnames.len() {
        return true;
    }
    let mut a = pn.clone();
    let mut b = fnames.clone();
    a.sort_unstable();
    b.sort_unstable();
    if a != b {
        return true;
    }
    pn != fnames
}

fn resolve_wrapper_target(
    meta: &Metadata,
    ts: &mut TypeSystem<'_>,
    td: &TypeDefinition,
    ifaces: &[String],
) -> (Option<String>, Option<usize>) {
    for iface_ix in meta.interfaces_of(td) {
        if let Some((tname, tidx)) = generic_wrapper_target(ts, iface_ix) {
            return (Some(tname), tidx);
        }
    }
    for iface in ifaces {
        if let Some(inner) = extract_wrapper_targ(iface) {
            let tidx = find_typedef_by_simple_name(meta, &inner);
            return (Some(inner), tidx);
        }
    }
    let leaf = meta.string(td.name_index).unwrap_or_default();
    if let Some(rest) = leaf.strip_suffix("ForMemoryPack") {
        if let Some(simple) = rest.rsplit('_').next() {
            if !simple.is_empty() {
                let tidx = find_typedef_by_simple_name(meta, simple);
                return (Some(simple.to_string()), tidx);
            }
        }
    }
    (None, None)
}

fn generic_wrapper_target(
    ts: &mut TypeSystem<'_>,
    iface_type_index: i32,
) -> Option<(String, Option<usize>)> {
    let t = ts.type_at_index(iface_type_index).ok()?;
    if TypeEnum::from_u8(t.type_enum()) != TypeEnum::GenericInst {
        return None;
    }
    let (base_leaf, arg_indices) = generic_inst_leaf_and_arg_indices(ts, t.data)?;
    if !base_leaf.starts_with("IMemoryPackDeSerializeWrapper") {
        return None;
    }
    let arg0 = *arg_indices.first()?;
    let tidx = ts.typedef_index_of(arg0).map(|i| i as usize);
    let tname = ts.name_of_index(arg0);
    Some((tname, tidx))
}

/// Resolve GenericInst → (base type leaf name, type-table indices of args).
fn generic_inst_leaf_and_arg_indices(
    ts: &TypeSystem<'_>,
    generic_class_va: u64,
) -> Option<(String, Vec<i32>)> {
    let type_va = ts.pe.read_u64_va(generic_class_va)?;
    let class_inst_va = ts.pe.read_u64_va(generic_class_va + 8)?;
    let base_ty = ts.read_type_at_va(type_va).ok()?;
    let base_leaf = match TypeEnum::from_u8(base_ty.type_enum()) {
        TypeEnum::Class | TypeEnum::ValueType | TypeEnum::TypeIndex => {
            let idx = base_ty.klass_index();
            ts.meta
                .type_defs
                .get(idx as usize)
                .and_then(|td| ts.meta.string(td.name_index).ok())
                .unwrap_or_else(|| format!("/*td:{idx}*/"))
        }
        _ => return None,
    };
    if class_inst_va == 0 {
        return Some((base_leaf, Vec::new()));
    }
    let argc = ts.pe.read_u64_va(class_inst_va)? as usize;
    let argv = ts.pe.read_u64_va(class_inst_va + 8)?;
    let mut args = Vec::with_capacity(argc.min(16));
    for i in 0..argc.min(16) {
        let arg_ty_va = ts.pe.read_u64_va(argv + (i as u64) * 8)?;
        let idx = ts.type_ptrs.iter().position(|&p| p == arg_ty_va)? as i32;
        args.push(idx);
    }
    Some((base_leaf, args))
}

fn find_nested_formatter(
    meta: &Metadata,
    td: &TypeDefinition,
) -> (bool, Option<usize>, Option<String>) {
    for child in meta.nested_children(td) {
        let ctd = &meta.type_defs[child];
        let cname = meta.string(ctd.name_index).unwrap_or_default();
        if cname.contains("Formatter") {
            let full = meta.type_name(ctd).unwrap_or(cname);
            return (true, Some(child), Some(full));
        }
    }
    (false, None, None)
}

fn method_flags(meta: &Metadata, td: &TypeDefinition) -> (bool, bool) {
    let mut has_deser = false;
    let mut has_reg = false;
    if td.method_count == 0 || td.method_index < 0 {
        return (false, false);
    }
    let start = td.method_index as usize;
    for mi in start..start + td.method_count as usize {
        let Some(m) = meta.methods.get(mi) else {
            break;
        };
        let n = meta.string(m.name_index).unwrap_or_default();
        if n == "Deserialize" {
            has_deser = true;
        }
        if n == "RegisterFormatter" {
            has_reg = true;
        }
    }
    (has_deser, has_reg)
}

fn strip_dunder(raw: &str) -> Option<String> {
    let s = raw.strip_prefix("__")?.strip_suffix("__")?;
    if s.is_empty() {
        return None;
    }
    Some(s.to_string())
}

fn is_serialize_marker_iface(name: &str) -> bool {
    let leaf = name.rsplit('.').next().unwrap_or(name);
    leaf == "IMemoryPackSerialize" || leaf.starts_with("IMemoryPackSerialize`")
}

fn extract_wrapper_targ(iface: &str) -> Option<String> {
    let leaf = iface.rsplit('.').next().unwrap_or(iface);
    if !leaf.starts_with("IMemoryPackDeSerializeWrapper") {
        return None;
    }
    let open = leaf.find('<')?;
    let close = leaf.rfind('>')?;
    if close <= open + 1 {
        return None;
    }
    Some(leaf[open + 1..close].to_string())
}

fn find_typedef_by_simple_name(meta: &Metadata, simple: &str) -> Option<usize> {
    for (i, td) in meta.type_defs.iter().enumerate() {
        let Ok(n) = meta.string(td.name_index) else {
            continue;
        };
        if n == simple && !n.ends_with("ForMemoryPack") && !n.contains("Formatter") {
            return Some(i);
        }
    }
    None
}

fn parent_info(
    ts: &mut TypeSystem<'_>,
    target_td: usize,
) -> Option<(Option<String>, Option<usize>)> {
    let parent_index = ts.meta.type_defs.get(target_td)?.parent_index;
    if parent_index < 0 {
        return None;
    }
    let name = ts.name_of_index(parent_index);
    if layout::skip_parent_name(&name) || name.contains("/*") {
        return None;
    }
    let parent_td = ts.typedef_index_of(parent_index).map(|i| i as usize);
    Some((Some(name), parent_td))
}

fn apply_inherited_wire(
    wrappers: &mut [WrapperSchema],
    ts: &TypeSystem<'_>,
    instance_size: &[i32],
) {
    let by_target: HashMap<usize, usize> = wrappers
        .iter()
        .enumerate()
        .filter_map(|(i, w)| w.target_typedef_index.map(|t| (t, i)))
        .collect();
    let mut by_name: HashMap<String, usize> = HashMap::new();
    let mut name_ambiguous = HashSet::new();
    for (i, w) in wrappers.iter().enumerate() {
        let Some(name) = w.target_type.as_ref() else {
            continue;
        };
        if name_ambiguous.contains(name) {
            continue;
        }
        if by_name.insert(name.clone(), i).is_some() {
            by_name.remove(name);
            name_ambiguous.insert(name.clone());
        }
    }
    // Closed generic parents (BlackboardInt → ParamBase<int,int>) and
    // NodeBase under an overlapping ActionBase must settle in parent-first
    // order. Repeat until the wires stop growing.
    for _ in 0..8 {
        let wires: Vec<Vec<MemberProp>> = wrappers.iter().map(|w| w.wire_order.clone()).collect();
        let mut changed = false;
        for i in 0..wrappers.len() {
            let Some(tidx) = wrappers[i].target_typedef_index else {
                continue;
            };
            let min_off = wrappers[i]
                .members_from_target_fields
                .iter()
                .filter_map(|f| f.offset)
                .min();
            let chain = base_chain(ts, instance_size, tidx, min_off);
            let mut merged = Vec::new();
            let mut seen = HashSet::new();
            for base_td in chain {
                let Some(&wi) = by_target.get(&base_td) else {
                    continue;
                };
                for m in &wires[wi] {
                    if seen.insert(m.name.clone()) {
                        merged.push(m.clone());
                    }
                }
            }
            if let Some(pname) = wrappers[i].parent_type.as_ref() {
                if let Some(&wi) = by_name.get(pname) {
                    for m in &wires[wi] {
                        if seen.insert(m.name.clone()) {
                            merged.push(m.clone());
                        }
                    }
                }
            }
            for m in &wires[i] {
                if seen.insert(m.name.clone()) {
                    merged.push(m.clone());
                }
            }
            if merged.len() > wrappers[i].wire_order.len() {
                wrappers[i].wire_includes_base = true;
                wrappers[i].wire_order = merged.clone();
                wrappers[i].members_from_wrapper_props = merged;
                changed = true;
            }
        }
        if !changed {
            break;
        }
    }
}

fn base_chain(
    ts: &TypeSystem<'_>,
    instance_size: &[i32],
    target_td: usize,
    derived_min_offset: Option<i32>,
) -> Vec<usize> {
    let mut chain = Vec::new();
    let mut current = target_td as i32;
    for _ in 0..8 {
        let Some(parent) = ts.next_parent_typedef(current) else {
            break;
        };
        let name = ts.typedef_full_name(parent);
        if layout::skip_parent_name(&name) {
            break;
        }
        let parent_size = instance_size.get(parent as usize).copied().unwrap_or(0);
        if let Some(min_off) = derived_min_offset {
            if min_off < parent_size && min_off > 0 {
                // Own fields sit inside this parent's instance (ActionBase
                // runtime fields after NodeBase). Skip it, keep walking to
                // ancestors that still fit — NodeBase at size 96 with min 96.
                current = parent;
                continue;
            }
        }
        chain.push(parent as usize);
        current = parent;
    }
    chain.reverse();
    chain
}

fn apply_native_member_counts(
    wrappers: &mut [WrapperSchema],
    counts: &BTreeMap<String, i32>,
) {
    for w in wrappers {
        if let Some(fmt) = &w.formatter_type {
            if let Some(&n) = counts.get(fmt) {
                w.native_member_count = Some(n);
            }
        }
    }
}

fn collect_type_names(ty: &str, out: &mut HashSet<String>) {
    out.insert(ty.to_string());
    if let Some((_, args)) = crate::wire::split_generic(ty) {
        for a in args {
            collect_type_names(&a, out);
        }
    }
    if let Some(elem) = ty.strip_suffix("[]") {
        collect_type_names(elem.trim(), out);
    }
}

fn collect_missing(
    wrappers: &[WrapperSchema],
    layouts: &TypeLayouts,
    unions: &[UnionTable],
) -> serde_json::Value {
    let mut known = HashSet::new();
    for w in wrappers {
        if let Some(t) = &w.target_type {
            known.insert(t.clone());
        }
    }
    for k in layouts.enums.keys() {
        known.insert(k.clone());
    }
    for k in layouts.unmanaged.keys() {
        known.insert(k.clone());
    }
    for u in unions {
        known.insert(u.static_type.clone());
        for v in u.tags.values() {
            known.insert(v.clone());
        }
    }
    for prim in [
        "bool", "byte", "sbyte", "short", "ushort", "int", "uint", "long", "ulong", "float",
        "double", "char", "string", "object",
    ] {
        known.insert(prim.into());
    }
    let mut missing = Vec::new();
    for w in wrappers {
        for m in &w.wire_order {
            let names = referenced_named(&m.ty);
            for n in names {
                if known.contains(&n) || crate::wire::classify(&n).label() != "named" {
                    continue;
                }
                missing.push(serde_json::json!({
                    "type": n,
                    "referenced_by": w.target_type,
                    "field": m.name,
                    "status": "unresolved",
                }));
            }
        }
    }
    serde_json::json!({
        "format": "beyond-mp-missing/1",
        "count": missing.len(),
        "note": "Types that appear on a wire but have no wrapper, enum, unmanaged layout, or union. A decoder must fail these instead of guessing.",
        "items": missing,
    })
}

fn referenced_named(ty: &str) -> Vec<String> {
    match crate::wire::classify(ty) {
        crate::wire::WireKind::Named(n) => vec![n],
        crate::wire::WireKind::List { elem } => referenced_named(&elem),
        crate::wire::WireKind::Dictionary { key, value } => {
            let mut v = referenced_named(&key);
            v.extend(referenced_named(&value));
            v
        }
        crate::wire::WireKind::Nullable { inner } => referenced_named(&inner),
        _ => Vec::new(),
    }
}

fn build_td_image_map(meta: &Metadata) -> Vec<String> {
    let mut td_image = vec![String::new(); meta.type_defs.len()];
    for img in &meta.images {
        let name = meta.image_name(img).unwrap_or_default();
        let start = img.type_start.max(0) as usize;
        let end = (start + img.type_count as usize).min(meta.type_defs.len());
        for slot in td_image.iter_mut().take(end).skip(start) {
            *slot = name.clone();
        }
    }
    td_image
}

/// Endfield property method refs are offsets into the declaring type's method
/// list when they fall in `0..method_count`. Otherwise they are global MethodIndexes.
fn method_ref(td: &TypeDefinition, idx: i32) -> i32 {
    if idx < 0 {
        return idx;
    }
    if (idx as u32) < td.method_count as u32 && td.method_index >= 0 {
        td.method_index.saturating_add(idx)
    } else {
        idx
    }
}

fn property_type_name(
    meta: &Metadata,
    ts: &mut TypeSystem<'_>,
    td: &TypeDefinition,
    get: i32,
    set: i32,
) -> String {
    let get = method_ref(td, get);
    let set = method_ref(td, set);
    if get >= 0 {
        if let Some(m) = meta.methods.get(get as usize) {
            return ts.name_of_index(m.return_type);
        }
    }
    if set >= 0 {
        if let Some(m) = meta.methods.get(set as usize) {
            if m.parameter_count > 0 && m.parameter_start >= 0 {
                let pidx = m.parameter_start as usize;
                if let Some(p) = meta.parameters.get(pidx) {
                    return ts.name_of_index(p.type_index);
                }
            }
        }
    }
    "object".into()
}

pub fn output_readme(counts: &Counts, unions: &[UnionTable], reg_source: &str) -> String {
    let union_n = unions.len();
    format!(
        r#"# MemoryPack schema export / MemoryPack schema 导出

Registration: `{reg_source}`

本次统计：wrappers = {wrappers}，IMemoryPackSerialize markers = {markers}，与目标字段序不同 = {differ}，with formatter = {fmt}，native unions = {union_n}。

## 中文（必读：字节序）

Endfield 配置包走 **Beyond.MemoryPack**，**不是**经典 `[MemoryPackable]` CustomAttribute。

- 标记接口：`IMemoryPackSerialize`
- 包装类型：主要在 `MemoryPack.Beyond.dll`，名如 `…ForMemoryPack`，实现 `IMemoryPackDeSerializeWrapper` / `IMemoryPackDeSerializeWrapper<T>`
- 序列化成员：包装类型上的 `__字段名__` 属性（多为只有 setter）
- 嵌套 `*Formatter` + 静态 `Deserialize` / `RegisterFormatter`
- 字符串在线上是 **Beyond StringPool int32 id**（`-1` 空串，`-2` null），不是内联 UTF-8

### 线上字节序

`*ForMemoryPack.Deserialize` **按包装类型 `__name__` 属性在 metadata 中的顺序**读写成员（常见为字母序）。

**不是**目标类型实例字段的声明顺序。

已用 GameAssembly 反汇编确认的例子：

- `CharacterAIConfigDataForMemoryPack.Deserialize` @ `0x18C49443C`：memberCount=`0x0d`，setter 顺序与 `__prop__` 一致，与 `CharacterAIConfigData` 字段序不同
- `FKeyframe.Deserialize` @ `0x18C3E89F0`：按属性序写，与 struct 字段布局不同
- `AABB.Deserialize` @ `0x18C842B90`：先 `max` 后 `min`

| 字段 | 含义 |
|------|------|
| `wire_order` / `members_from_wrapper_props` | **规范线上顺序**（用于反序列化） |
| `members_from_target_fields` | 仅作字段布局/offset 参考，**不是** wire order |
| `orders_differ` | 两套名字集合或顺序不一致 |

不伪造 CustomAttribute。

## English

Canonical packed member order = wrapper `__name__` property metadata order (`wire_order`).
That order matches native `Deserialize` (often alphabetical). Target instance fields are layout/offset reference only.
Strings are StringPool ids. There are no classic MemoryPack custom attributes in this game.

## Files

| File | Role |
|------|------|
| `protocol.json` | Object / collection / union / string / dictionary-v2 read operations. |
| `schemas.json` | One object per `*ForMemoryPack` wrapper. **`wire_order` is the binary member order**, including `_` members and declared base fields. |
| `enums.json` | Enum underlying type and width from metadata `value__`. |
| `unmanaged.json` | Blittable valuetype payloads (no object header). |
| `unions.json` | Tag → type maps recovered from Formatter.Deserialize jumptables, with evidence. |
| `union-map.json` | Decoder-ready `{{ static_type: {{ tag: concrete }} }}`. Unique leaf names are included; ambiguous `Data` is not. |
| `missing.json` | Referenced types with no declared rule. A decoder must not guess these. |
| `type_index.json` | `by_basename["X"]` and `by_type["Beyond….X"]`. |
| `type.cs` | Compact dump. Field order is `wire_order`. |
| `README.md` | This note |

## BydTools handoff

1. Dump VFS JsonData (ChaCha20 is already implemented in BydTools; this repo does not decrypt VFS):

```text
dotnet BydTools.CLI.dll vfs --input <Endfield>/Endfield_Data/StreamingAssets --platform pc --blocktype JsonData --output vfs-json
```

2. Sniff each virtual file. A leading `{{` or `[` (after whitespace) is UTF-8 JSON. Anything else with a small member-count header is MemoryPack, even when the name ends in `.json`. Do not feed U8/Webview `.bin` or Table SparkBuffer `.bytes` to these schemas.

3. Map `…/{{Name}}.json` to `type_index.json` → `by_basename["{{Name}}"]`. Prefer a `serialize_marker` entry when several types share a leaf name. Read members in `wire_order`, not `members_from_target_fields`.

4. Wire sketch (Beyond.MemoryPack / MemoryPack object header):

```text
byte   memberCount     ; 255 = null, 250 = wide (next int32 is the count)
members in wire_order:
  bool                 1 byte
  int/uint/float       4 bytes
  long/ulong/double    8 bytes
  string               int32 pool id (-1 empty, -2 null)
  enum                 width from enums.json (not assumed int32)
  List / T[]           int32 count (-1 null) then elements
  Dictionary           int32 count (-1 null) then key, value pairs
  SerializeFieldDictionary  u8 header (255 null) then count; 0 is present
  nested *ForMemoryPack  same object header
UnityEngine.Vector2/3  packed floats (8 / 12); Vector4/Quaternion/Color = 16
```

5. Resolve pool ids with VFS `Data/Json/StringPool/StringPool*.byte` (magic `USPC`). Global id = `block_index * len(StringPool0) + local_index`.

Trial shape: `BambooRaftTaskTable` is memberCount 1, one `Dictionary<ulong, BambooRaftTaskData>`. `BambooRaftTaskData.wire_order` is `questConfigs`, then `raftId` (`orders_differ` is true).

BydTools owns the actual MemoryPack reader (unions, formatters, StringPool). `beyond-mp-schema decode` is an experimental sample for simple objects only; it is not part of the export contract.
"#,
        reg_source = reg_source,
        wrappers = counts.wrappers,
        markers = counts.serialize_markers,
        differ = counts.wrappers_orders_differ_from_target_fields,
        fmt = counts.wrappers_with_formatter,
        union_n = union_n,
    )
}
