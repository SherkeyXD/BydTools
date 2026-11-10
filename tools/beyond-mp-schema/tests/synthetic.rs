//! End-to-end export against a tiny Endfield-shaped metadata image.
//!
//! Proves wrapper `__prop__` order (not target field order), generic
//! `IMemoryPackDeSerializeWrapper<T>` target resolution, formatter discovery,
//! and field offsets — without a full GameAssembly.

use beyond_mp_schema::metadata::Metadata;
use beyond_mp_schema::pe::{PeImage, SectionInfo};
use beyond_mp_schema::reg::{locate_metadata_registration, LocateOptions};
use beyond_mp_schema::schema::{export_bundle, ORDER_NOTE, PURPOSE, WIRE_ORDER_SOURCE};

const IMAGE_BASE: u64 = 0x1_8000_0000;
const SECTION_RVA: u64 = 0x1000;
const RAW_PTR: usize = 0x200;

#[test]
fn exports_aabb_wire_order_from_synthetic_metadata() {
    let meta_bytes = build_metadata();
    let meta = Metadata::parse(meta_bytes).expect("parse synthetic metadata");
    assert_eq!(meta.type_defs.len(), 5);

    let (pe, _) = build_pe(meta.type_defs.len());
    let located = locate_metadata_registration(
        &pe,
        meta.type_defs.len() as u64,
        &LocateOptions {
            force_scan: false,
            known_rva: Some(SECTION_RVA),
        },
    )
    .expect("known rva");
    assert_eq!(located.source, "known-rva");

    let scanned = locate_metadata_registration(
        &pe,
        meta.type_defs.len() as u64,
        &LocateOptions {
            force_scan: true,
            known_rva: None,
        },
    )
    .expect("scan");
    assert_eq!(scanned.source, "scan");
    assert_eq!(scanned.reg.va, located.reg.va);

    let bundle = export_bundle(&meta, &pe, &located.reg, located.source).expect("export");
    let doc = &bundle.schemas;
    assert_eq!(doc.purpose, PURPOSE);
    assert_eq!(doc.wire_order_rule, WIRE_ORDER_SOURCE);
    assert_eq!(doc.order_caveat, ORDER_NOTE);
    assert_eq!(doc.counts.wrappers, 1);
    assert_eq!(doc.counts.serialize_markers, 1);
    assert_eq!(doc.counts.wrappers_orders_differ_from_target_fields, 1);
    assert_eq!(doc.counts.wrappers_with_formatter, 1);

    let w = &doc.wrappers[0];
    assert_eq!(w.wrapper_type, "Beyond.MemoryPack.Beyond_AABBForMemoryPack");
    assert_eq!(w.target_type.as_deref(), Some("Beyond.AABB"));
    assert_eq!(w.assembly, "MemoryPack.Beyond.dll");
    assert_eq!(w.target_assembly.as_deref(), Some("Common.Beyond.dll"));
    assert_eq!(w.typedef_index, 0);
    assert_eq!(w.target_typedef_index, Some(4));
    assert!(w.orders_differ);
    assert!(w.has_formatter);
    assert_eq!(
        w.formatter_type.as_deref(),
        Some("Beyond_AABBForMemoryPackFormatter")
    );
    assert_eq!(w.formatter_typedef, Some(1));
    assert!(w.has_deserialize);
    assert!(w.has_register_formatter);
    assert!(w
        .interfaces
        .iter()
        .any(|i| { i == "Beyond.MemoryPack.IMemoryPackDeSerializeWrapper`1<Beyond.AABB>" }));

    assert_eq!(w.wire_order.len(), 2);
    assert_eq!(w.wire_order[0].name, "max");
    assert_eq!(w.wire_order[0].raw_property, "__max__");
    assert_eq!(w.wire_order[0].ty, "float");
    assert_eq!(w.wire_order[0].property_index, 1);
    assert!(!w.wire_order[0].has_getter);
    assert!(w.wire_order[0].has_setter);
    assert_eq!(w.wire_order[1].name, "min");
    assert_eq!(w.wire_order[1].property_index, 2);
    assert_eq!(w.members_from_wrapper_props, w.wire_order);

    assert_eq!(w.members_from_target_fields[0].name, "min");
    assert_eq!(w.members_from_target_fields[0].offset, Some(16));
    assert!(w.members_from_target_fields[0].is_public);
    assert_eq!(w.members_from_target_fields[1].name, "max");
    assert_eq!(w.members_from_target_fields[1].offset, Some(20));

    let marker = &doc.serialize_markers[0];
    assert_eq!(marker.type_name, "Beyond.AABB");
    assert_eq!(marker.assembly, "Common.Beyond.dll");
    assert_eq!(marker.instance_field_count, 2);
    assert_eq!(
        marker.matching_wrapper.as_deref(),
        Some(w.wrapper_type.as_str())
    );
    assert!(marker
        .interfaces
        .iter()
        .any(|i| i == "Beyond.MemoryPack.IMemoryPackSerialize"));

    let index = &bundle.type_index;
    assert_eq!(index["by_basename"]["AABB"][0], "Beyond.AABB");
    assert_eq!(index["by_type"]["Beyond.AABB"]["serialize_marker"], true);
    assert_eq!(
        index["by_type"]["Beyond.AABB"]["entries"][0]["wire"][0]["name"],
        "max"
    );
    assert_eq!(
        index["by_type"]["Beyond.AABB"]["entries"][0]["wire"][0]["kind"],
        "float32"
    );
    assert!(bundle.readme.contains("wire_order"));
    assert!(bundle.readme.contains("BydTools"));
    assert!(bundle.readme.contains("type.cs"));
    assert!(bundle.type_cs.contains("public float max; // wire 0"));
    assert!(bundle.type_cs.contains("public float min; // wire 1"));
    assert!(bundle.type_cs.contains("Beyond_AABBForMemoryPack"));
}

fn build_pe(type_def_count: usize) -> (PeImage, u64) {
    let mut blob = Rdata::new(IMAGE_BASE + SECTION_RVA);
    let (reg_off, reg_va) = blob.alloc(0x80);
    assert_eq!(reg_off, 0);

    let (types_off, types_va) = blob.alloc(6 * 8);
    let (field_ptrs_off, field_ptrs_va) = blob.alloc(type_def_count * 8);
    let (_size_ptrs_off, size_ptrs_va) = blob.alloc(type_def_count * 8);
    let (offs_off, offs_va) = blob.alloc(8);
    blob.set_i32(offs_off, 16);
    blob.set_i32(offs_off + 4, 20);

    // Il2CppType slots. data / bits filled after generic-class VA is known.
    let mut type_vas = Vec::new();
    let mut type_offs = Vec::new();
    for _ in 0..6 {
        let (off, va) = blob.alloc(12);
        type_offs.push(off);
        type_vas.push(va);
    }
    let (gclass_off, gclass_va) = blob.alloc(16);
    let (ginst_off, ginst_va) = blob.alloc(16);
    let (argv_off, argv_va) = blob.alloc(8);

    // void, public float, class AABB, class IMemoryPackSerialize, generic inst, generic def class
    write_ty(&mut blob, type_offs[0], 0, bits(1, 0));
    write_ty(&mut blob, type_offs[1], 0, bits(12, 0x0006));
    write_ty(&mut blob, type_offs[2], 4, bits(18, 0));
    write_ty(&mut blob, type_offs[3], 3, bits(18, 0));
    write_ty(&mut blob, type_offs[4], gclass_va, bits(21, 0));
    write_ty(&mut blob, type_offs[5], 2, bits(18, 0));

    blob.set_u64(gclass_off, type_vas[5]);
    blob.set_u64(gclass_off + 8, ginst_va);
    blob.set_u64(ginst_off, 1);
    blob.set_u64(ginst_off + 8, argv_va);
    blob.set_u64(argv_off, type_vas[2]);

    for (i, va) in type_vas.iter().enumerate() {
        blob.set_u64(types_off + i * 8, *va);
    }
    // fieldOffsets[4] = AABB
    blob.set_u64(field_ptrs_off + 4 * 8, offs_va);

    blob.set_u64(reg_off, 1);
    blob.set_u64(reg_off + 0x10, 1);
    blob.set_u64(reg_off + 0x30, 6);
    blob.set_u64(reg_off + 0x38, types_va);
    blob.set_u64(reg_off + 0x50, type_def_count as u64);
    blob.set_u64(reg_off + 0x58, field_ptrs_va);
    blob.set_u64(reg_off + 0x60, type_def_count as u64);
    blob.set_u64(reg_off + 0x68, size_ptrs_va);

    let mut data = vec![0u8; RAW_PTR];
    data.extend_from_slice(&blob.buf);
    let raw_size = blob.buf.len() as u32;
    let pe = PeImage::from_parts(
        IMAGE_BASE,
        vec![SectionInfo {
            name: ".rdata".into(),
            virtual_address: SECTION_RVA as u32,
            virtual_size: raw_size,
            raw_ptr: RAW_PTR as u32,
            raw_size,
        }],
        data,
    );
    (pe, reg_va)
}

fn bits(type_enum: u32, attrs: u32) -> u32 {
    (type_enum << 16) | (attrs & 0xffff)
}

fn write_ty(blob: &mut Rdata, off: usize, data: u64, bits: u32) {
    blob.set_u64(off, data);
    blob.set_u32(off + 8, bits);
}

struct Rdata {
    base: u64,
    buf: Vec<u8>,
}

impl Rdata {
    fn new(base: u64) -> Self {
        Self {
            base,
            buf: Vec::new(),
        }
    }

    fn alloc(&mut self, n: usize) -> (usize, u64) {
        while self.buf.len() % 8 != 0 {
            self.buf.push(0);
        }
        let off = self.buf.len();
        self.buf.resize(off + n, 0);
        (off, self.base + off as u64)
    }

    fn set_u64(&mut self, off: usize, v: u64) {
        self.buf[off..off + 8].copy_from_slice(&v.to_le_bytes());
    }

    fn set_u32(&mut self, off: usize, v: u32) {
        self.buf[off..off + 4].copy_from_slice(&v.to_le_bytes());
    }

    fn set_i32(&mut self, off: usize, v: i32) {
        self.set_u32(off, v as u32);
    }
}

fn build_metadata() -> Vec<u8> {
    let mut strings = vec![0u8];
    let mut intern = |s: &str| -> i32 {
        if s.is_empty() {
            return 0;
        }
        let i = strings.len() as i32;
        strings.extend_from_slice(s.as_bytes());
        strings.push(0);
        i
    };

    let s_mp_dll = intern("MemoryPack.Beyond.dll");
    let s_ns_mp = intern("Beyond.MemoryPack");
    let s_wrapper = intern("Beyond_AABBForMemoryPack");
    let s_common = intern("Common.Beyond.dll");
    let s_ns = intern("Beyond");
    let s_aabb = intern("AABB");
    let s_inst = intern("__instance__");
    let s_pmax = intern("__max__");
    let s_pmin = intern("__min__");
    let s_min = intern("min");
    let s_max = intern("max");
    let s_set_max = intern("set_max");
    let s_set_min = intern("set_min");
    let s_deser = intern("Deserialize");
    let s_regf = intern("RegisterFormatter");
    let s_fmt = intern("Beyond_AABBForMemoryPackFormatter");
    let s_wrap_iface = intern("IMemoryPackDeSerializeWrapper`1");
    let s_marker = intern("IMemoryPackSerialize");

    let images = [image(s_mp_dll, 0, 4), image(s_common, 4, 1)].concat();

    let typedefs = [
        typedef(Td {
            name: s_wrapper,
            ns: s_ns_mp,
            method_index: 0,
            method_count: 4,
            property_index: 0,
            property_count: 3,
            nested_type_index: 0,
            nested_type_count: 1,
            interfaces_index: 0,
            interfaces_count: 1,
            ..Td::default()
        }),
        typedef(Td {
            name: s_fmt,
            ..Td::default()
        }),
        typedef(Td {
            name: s_wrap_iface,
            ns: s_ns_mp,
            ..Td::default()
        }),
        typedef(Td {
            name: s_marker,
            ns: s_ns_mp,
            ..Td::default()
        }),
        typedef(Td {
            name: s_aabb,
            ns: s_ns,
            field_index: 0,
            field_count: 2,
            interfaces_index: 1,
            interfaces_count: 1,
            bitfield: 1,
            ..Td::default()
        }),
    ]
    .concat();

    let methods = [
        method(s_set_max, 0, 0, 1),
        method(s_set_min, 0, 1, 1),
        method(s_deser, 0, -1, 0),
        method(s_regf, 0, -1, 0),
    ]
    .concat();

    let parameters = [param(1), param(1)].concat();
    let properties = [
        prop(s_inst, -1, -1),
        prop(s_pmax, -1, 0),
        prop(s_pmin, -1, 1),
    ]
    .concat();
    let fields = [field(s_min, 1), field(s_max, 1)].concat();
    let nested = i32s(&[1]);
    let interfaces = i32s(&[4, 3]);

    let mut file = vec![0u8; 0x110];
    file[0..4].copy_from_slice(&0xFAB1_1BAFu32.to_le_bytes());
    file[4..8].copy_from_slice(&29i32.to_le_bytes());

    let string_off = file.len() as i32;
    let string_size = strings.len() as i32;
    file.extend_from_slice(&strings);

    let (images_off, images_size) = append(&mut file, &images);
    let (td_off, td_size) = append(&mut file, &typedefs);
    let (methods_off, methods_size) = append(&mut file, &methods);
    let (params_off, params_size) = append(&mut file, &parameters);
    let (props_off, props_size) = append(&mut file, &properties);
    let (fields_off, fields_size) = append(&mut file, &fields);
    let (nested_off, nested_size) = append(&mut file, &nested);
    let (ifaces_off, ifaces_size) = append(&mut file, &interfaces);

    // string_literal must sit at 0x110. Other empty sections can share that offset.
    put_section(&mut file, 0, 0x110, 0);
    put_section(&mut file, 2, string_off, string_size);
    put_section(&mut file, 4, props_off, props_size);
    put_section(&mut file, 5, methods_off, methods_size);
    put_section(&mut file, 10, params_off, params_size);
    put_section(&mut file, 11, fields_off, fields_size);
    put_section(&mut file, 15, nested_off, nested_size);
    put_section(&mut file, 16, ifaces_off, ifaces_size);
    put_section(&mut file, 19, td_off, td_size);
    put_section(&mut file, 20, images_off, images_size);
    for idx in [
        1, 3, 6, 7, 8, 9, 12, 13, 14, 17, 18, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32,
    ] {
        put_section(&mut file, idx, 0x110, 0);
    }
    file
}

fn append(file: &mut Vec<u8>, bytes: &[u8]) -> (i32, i32) {
    let off = file.len() as i32;
    file.extend_from_slice(bytes);
    (off, bytes.len() as i32)
}

fn put_section(header: &mut [u8], index: usize, off: i32, size: i32) {
    let at = 8 + index * 8;
    header[at..at + 4].copy_from_slice(&off.to_le_bytes());
    header[at + 4..at + 8].copy_from_slice(&size.to_le_bytes());
}

fn i32s(vals: &[i32]) -> Vec<u8> {
    vals.iter().flat_map(|v| v.to_le_bytes()).collect()
}

fn image(name: i32, type_start: i32, type_count: u32) -> Vec<u8> {
    let mut b = vec![0u8; 0x28];
    put_i32(&mut b, 0, name);
    put_i32(&mut b, 8, type_start);
    put_u32(&mut b, 12, type_count);
    b
}

#[derive(Clone, Copy)]
struct Td {
    name: i32,
    ns: i32,
    field_index: i32,
    method_index: i32,
    property_index: i32,
    nested_type_index: i32,
    interfaces_index: i32,
    method_count: u16,
    property_count: u16,
    field_count: u16,
    nested_type_count: u16,
    interfaces_count: u16,
    bitfield: u32,
}

impl Default for Td {
    fn default() -> Self {
        Self {
            name: 0,
            ns: 0,
            field_index: -1,
            method_index: -1,
            property_index: -1,
            nested_type_index: -1,
            interfaces_index: -1,
            method_count: 0,
            property_count: 0,
            field_count: 0,
            nested_type_count: 0,
            interfaces_count: 0,
            bitfield: 0,
        }
    }
}

fn typedef(td: Td) -> Vec<u8> {
    let mut b = vec![0u8; 0x5c];
    put_i32(&mut b, 0x00, td.name);
    put_i32(&mut b, 0x04, td.ns);
    put_i32(&mut b, 0x08, -1);
    put_i32(&mut b, 0x0c, -1);
    put_i32(&mut b, 0x10, -1);
    put_i32(&mut b, 0x14, -1);
    put_i32(&mut b, 0x18, -1);
    put_i32(&mut b, 0x20, td.field_index);
    put_i32(&mut b, 0x24, td.method_index);
    put_i32(&mut b, 0x28, -1);
    put_i32(&mut b, 0x2c, td.property_index);
    put_i32(&mut b, 0x30, td.nested_type_index);
    put_i32(&mut b, 0x34, td.interfaces_index);
    put_i32(&mut b, 0x38, -1);
    put_i32(&mut b, 0x3c, -1);
    put_u16(&mut b, 0x44, td.method_count);
    put_u16(&mut b, 0x46, td.property_count);
    put_u16(&mut b, 0x48, td.field_count);
    put_u16(&mut b, 0x4c, td.nested_type_count);
    put_u16(&mut b, 0x50, td.interfaces_count);
    put_u32(&mut b, 0x54, td.bitfield);
    b
}

fn method(name: i32, ret: i32, param_start: i32, param_count: u16) -> Vec<u8> {
    let mut b = vec![0u8; 0x20];
    put_i32(&mut b, 0x00, name);
    put_i32(&mut b, 0x04, 0);
    put_i32(&mut b, 0x08, ret);
    put_i32(&mut b, 0x0c, param_start);
    put_i32(&mut b, 0x10, -1);
    put_u16(&mut b, 0x1e, param_count);
    b
}

fn param(type_index: i32) -> Vec<u8> {
    let mut b = vec![0u8; 0x0c];
    put_i32(&mut b, 8, type_index);
    b
}

fn prop(name: i32, get: i32, set: i32) -> Vec<u8> {
    let mut b = vec![0u8; 0x14];
    put_i32(&mut b, 0, name);
    put_i32(&mut b, 4, get);
    put_i32(&mut b, 8, set);
    b
}

fn field(name: i32, type_index: i32) -> Vec<u8> {
    let mut b = vec![0u8; 0x0c];
    put_i32(&mut b, 0, name);
    put_i32(&mut b, 4, type_index);
    b
}

fn put_i32(buf: &mut [u8], off: usize, v: i32) {
    buf[off..off + 4].copy_from_slice(&v.to_le_bytes());
}

fn put_u32(buf: &mut [u8], off: usize, v: u32) {
    buf[off..off + 4].copy_from_slice(&v.to_le_bytes());
}

fn put_u16(buf: &mut [u8], off: usize, v: u16) {
    buf[off..off + 2].copy_from_slice(&v.to_le_bytes());
}
