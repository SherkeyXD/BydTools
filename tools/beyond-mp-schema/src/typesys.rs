//! Il2CppType → C# name resolver (Endfield / metadata v29 / x64).
//!
//! Names match the strings stored on gold `schemas.json` members (`int`,
//! `Dictionary`2<…>`, `Beyond.Gameplay.X`).

use crate::metadata::Metadata;
use crate::pe::PeImage;
use anyhow::{bail, Context, Result};
use std::collections::HashMap;

/// Il2CppTypeEnum (byte values).
#[repr(u8)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TypeEnum {
    End = 0,
    Void = 1,
    Boolean = 2,
    Char = 3,
    I1 = 4,
    U1 = 5,
    I2 = 6,
    U2 = 7,
    I4 = 8,
    U4 = 9,
    I8 = 10,
    U8 = 11,
    R4 = 12,
    R8 = 13,
    String = 14,
    Ptr = 15,
    ByRef = 16,
    ValueType = 17,
    Class = 18,
    Var = 19,
    Array = 20,
    GenericInst = 21,
    TypedByRef = 22,
    I = 0x18,
    U = 0x19,
    FnPtr = 0x1b,
    Object = 0x1c,
    SzArray = 0x1d,
    MVar = 0x1e,
    Enum = 0x55,
    TypeIndex = 0xff,
}

impl TypeEnum {
    pub fn from_u8(v: u8) -> Self {
        match v {
            0 => Self::End,
            1 => Self::Void,
            2 => Self::Boolean,
            3 => Self::Char,
            4 => Self::I1,
            5 => Self::U1,
            6 => Self::I2,
            7 => Self::U2,
            8 => Self::I4,
            9 => Self::U4,
            10 => Self::I8,
            11 => Self::U8,
            12 => Self::R4,
            13 => Self::R8,
            14 => Self::String,
            15 => Self::Ptr,
            16 => Self::ByRef,
            17 => Self::ValueType,
            18 => Self::Class,
            19 => Self::Var,
            20 => Self::Array,
            21 => Self::GenericInst,
            22 => Self::TypedByRef,
            0x18 => Self::I,
            0x19 => Self::U,
            0x1b => Self::FnPtr,
            0x1c => Self::Object,
            0x1d => Self::SzArray,
            0x1e => Self::MVar,
            0x55 => Self::Enum,
            0xff => Self::TypeIndex,
            _ => Self::End,
        }
    }
}

/// On-disk Il2CppType (x64, v29): `ulong data` + `uint bits` (12 bytes).
#[derive(Debug, Clone, Copy)]
pub struct Il2CppType {
    pub data: u64,
    pub bits: u32,
}

impl Il2CppType {
    pub fn parse(buf: &[u8]) -> Self {
        Self {
            data: u64::from_le_bytes(buf[0..8].try_into().unwrap()),
            bits: u32::from_le_bytes(buf[8..12].try_into().unwrap()),
        }
    }

    pub fn type_enum(self) -> u8 {
        ((self.bits >> 16) & 0xff) as u8
    }

    pub fn byref(self) -> bool {
        ((self.bits >> 29) & 1) == 1
    }

    pub fn klass_index(self) -> i32 {
        self.data as i32
    }
}

pub struct TypeSystem<'a> {
    pub pe: &'a PeImage,
    pub meta: &'a Metadata,
    /// VA of each Il2CppType* (from MetadataRegistration.types).
    pub type_ptrs: Vec<u64>,
    cache: HashMap<i32, String>,
}

impl<'a> TypeSystem<'a> {
    pub fn load(
        pe: &'a PeImage,
        meta: &'a Metadata,
        types_va: u64,
        types_count: u64,
    ) -> Result<Self> {
        let type_ptrs = pe
            .read_u64_array_va(types_va, types_count as usize)
            .with_context(|| format!("read types array at {types_va:#x} count={types_count}"))?;
        Ok(Self {
            pe,
            meta,
            type_ptrs,
            cache: HashMap::new(),
        })
    }

    pub fn read_type_at_va(&self, va: u64) -> Result<Il2CppType> {
        let off = self
            .pe
            .va_to_offset(va)
            .with_context(|| format!("map Il2CppType VA {va:#x}"))?;
        if off + 12 > self.pe.data.len() {
            bail!("Il2CppType truncated at {va:#x}");
        }
        Ok(Il2CppType::parse(&self.pe.data[off..off + 12]))
    }

    pub fn type_at_index(&self, index: i32) -> Result<Il2CppType> {
        if index < 0 || index as usize >= self.type_ptrs.len() {
            bail!(
                "type index {index} out of range (0..{})",
                self.type_ptrs.len()
            );
        }
        self.read_type_at_va(self.type_ptrs[index as usize])
    }

    pub fn attrs_of_index(&self, index: i32) -> u32 {
        self.type_at_index(index)
            .map(|t| t.bits & 0xffff)
            .unwrap_or(0)
    }

    /// If `index` names a Class/ValueType/Enum, return its TypeDef index.
    pub fn typedef_index_of(&self, index: i32) -> Option<i32> {
        let t = self.type_at_index(index).ok()?;
        match TypeEnum::from_u8(t.type_enum()) {
            TypeEnum::Class | TypeEnum::ValueType | TypeEnum::Enum | TypeEnum::TypeIndex => {
                let idx = t.klass_index();
                if idx >= 0 && (idx as usize) < self.meta.type_defs.len() {
                    Some(idx)
                } else {
                    None
                }
            }
            _ => None,
        }
    }

    pub fn name_of_index(&mut self, index: i32) -> String {
        if let Some(s) = self.cache.get(&index) {
            return s.clone();
        }
        let name = self
            .name_of_index_uncached(index, 0)
            .unwrap_or_else(|_| format!("/*type:{index}*/object"));
        self.cache.insert(index, name.clone());
        name
    }

    fn name_of_index_uncached(&mut self, index: i32, depth: u8) -> Result<String> {
        if depth > 16 {
            return Ok("/*cycle*/object".into());
        }
        let t = self.type_at_index(index)?;
        self.name_of_type(t, depth)
    }

    fn name_of_type(&mut self, t: Il2CppType, depth: u8) -> Result<String> {
        let mut s = self.name_of_type_core(t, depth)?;
        if t.byref() {
            s.push('&');
        }
        Ok(s)
    }

    fn name_of_type_core(&mut self, t: Il2CppType, depth: u8) -> Result<String> {
        let e = TypeEnum::from_u8(t.type_enum());
        Ok(match e {
            TypeEnum::Void => "void".into(),
            TypeEnum::Boolean => "bool".into(),
            TypeEnum::Char => "char".into(),
            TypeEnum::I1 => "sbyte".into(),
            TypeEnum::U1 => "byte".into(),
            TypeEnum::I2 => "short".into(),
            TypeEnum::U2 => "ushort".into(),
            TypeEnum::I4 => "int".into(),
            TypeEnum::U4 => "uint".into(),
            TypeEnum::I8 => "long".into(),
            TypeEnum::U8 => "ulong".into(),
            TypeEnum::R4 => "float".into(),
            TypeEnum::R8 => "double".into(),
            TypeEnum::String => "string".into(),
            TypeEnum::Object => "object".into(),
            TypeEnum::TypedByRef => "TypedReference".into(),
            TypeEnum::I => "IntPtr".into(),
            TypeEnum::U => "UIntPtr".into(),
            TypeEnum::Var => generic_param_label(self.meta, t.klass_index(), false),
            TypeEnum::MVar => generic_param_label(self.meta, t.klass_index(), true),
            TypeEnum::FnPtr => "/*fnptr*/IntPtr".into(),
            TypeEnum::Class | TypeEnum::ValueType | TypeEnum::Enum | TypeEnum::TypeIndex => {
                self.typedef_full_name(t.klass_index())
            }
            TypeEnum::Ptr => {
                if let Ok(inner) = self.read_type_at_va(t.data) {
                    format!("{}*", self.name_of_type(inner, depth + 1)?)
                } else {
                    "void*".into()
                }
            }
            TypeEnum::ByRef => {
                if let Ok(inner) = self.read_type_at_va(t.data) {
                    format!("{}&", self.name_of_type_core(inner, depth + 1)?)
                } else {
                    "object&".into()
                }
            }
            TypeEnum::SzArray => {
                if let Ok(inner) = self.read_type_at_va(t.data) {
                    format!("{}[]", self.name_of_type(inner, depth + 1)?)
                } else {
                    "object[]".into()
                }
            }
            TypeEnum::Array => {
                if let Some(elem_va) = self.pe.read_u64_va(t.data) {
                    let rank = self
                        .pe
                        .va_to_offset(t.data + 8)
                        .and_then(|o| self.pe.data.get(o).copied())
                        .unwrap_or(1);
                    let commas = if rank <= 1 {
                        String::new()
                    } else {
                        ",".repeat((rank as usize) - 1)
                    };
                    if let Ok(inner) = self.read_type_at_va(elem_va) {
                        format!("{}[{commas}]", self.name_of_type(inner, depth + 1)?)
                    } else {
                        format!("object[{commas}]")
                    }
                } else {
                    "object[]".into()
                }
            }
            TypeEnum::GenericInst => self.name_generic_inst(t.data, depth)?,
            TypeEnum::End => "/*end*/object".into(),
        })
    }

    /// `Ns.Outer+Inner+Leaf`. Nested identity is required so unrelated `Data`
    /// wrappers do not collapse to one name.
    pub fn typedef_full_name(&self, td_index: i32) -> String {
        if td_index < 0 || td_index as usize >= self.meta.type_defs.len() {
            return format!("/*td:{td_index}*/object");
        }
        let mut chain = Vec::new();
        let mut current = td_index;
        for _ in 0..8 {
            if current < 0 || current as usize >= self.meta.type_defs.len() {
                break;
            }
            let td = &self.meta.type_defs[current as usize];
            let leaf = sanitize_ident(
                &self
                    .meta
                    .string(td.name_index)
                    .unwrap_or_else(|_| "?".into()),
            );
            chain.push((td.namespace_index, leaf, td.declaring_type_index));
            match self.declaring_typedef(current) {
                Some(parent) if parent != current => current = parent,
                _ => break,
            }
        }
        chain.reverse();
        let ns = chain
            .first()
            .map(|(ns_idx, _, _)| self.meta.string(*ns_idx).unwrap_or_default())
            .unwrap_or_default();
        let nested = chain
            .iter()
            .map(|(_, leaf, _)| leaf.as_str())
            .collect::<Vec<_>>()
            .join("+");
        if ns.is_empty() {
            nested
        } else {
            format!("{}.{}", sanitize_ns(&ns), nested)
        }
    }

    pub fn parent_typedef_index(&self, td_index: i32) -> Option<i32> {
        let td = self.meta.type_defs.get(td_index as usize)?;
        if td.parent_index < 0 {
            return None;
        }
        self.typedef_index_of(td.parent_index)
    }

    /// Class parent, or the generic type definition when parent is a GenericInst.
    /// `BlackboardDouble : BlackboardParamBase<float,double>` has no Class parent
    /// index; the inst still names a TypeDef whose closed wrapper holds the wire.
    pub fn next_parent_typedef(&self, td_index: i32) -> Option<i32> {
        let td = self.meta.type_defs.get(td_index as usize)?;
        if td.parent_index < 0 {
            return None;
        }
        self.typedef_index_of(td.parent_index)
            .or_else(|| self.generic_inst_typedef(td.parent_index))
    }

    pub fn generic_inst_typedef(&self, type_index: i32) -> Option<i32> {
        let t = self.type_at_index(type_index).ok()?;
        if TypeEnum::from_u8(t.type_enum()) != TypeEnum::GenericInst {
            return None;
        }
        let type_va = self.pe.read_u64_va(t.data)?;
        let base = self.read_type_at_va(type_va).ok()?;
        match TypeEnum::from_u8(base.type_enum()) {
            TypeEnum::Class | TypeEnum::ValueType | TypeEnum::Enum | TypeEnum::TypeIndex => {
                let idx = base.klass_index();
                if idx >= 0 && (idx as usize) < self.meta.type_defs.len() {
                    Some(idx)
                } else {
                    None
                }
            }
            _ => None,
        }
    }

    pub fn declaring_typedef(&self, td_index: i32) -> Option<i32> {
        let td = self.meta.type_defs.get(td_index as usize)?;
        if td.declaring_type_index < 0 {
            return None;
        }
        let parent = self.typedef_index_of(td.declaring_type_index)?;
        if parent == td_index {
            return None;
        }
        let ptd = self.meta.type_defs.get(parent as usize)?;
        if self
            .meta
            .nested_children(ptd)
            .iter()
            .any(|&c| c == td_index as usize)
        {
            Some(parent)
        } else {
            None
        }
    }

    fn name_generic_inst(&mut self, generic_class_va: u64, depth: u8) -> Result<String> {
        // Il2CppGenericClass (≥27): Type* at +0, class_inst* at +8 (context inline).
        let type_va = self
            .pe
            .read_u64_va(generic_class_va)
            .context("GenericClass.Type")?;
        let class_inst_va = self
            .pe
            .read_u64_va(generic_class_va + 8)
            .context("GenericClass.class_inst")?;
        let base_ty = self.read_type_at_va(type_va)?;
        let base_name = match TypeEnum::from_u8(base_ty.type_enum()) {
            TypeEnum::Class | TypeEnum::ValueType => {
                self.typedef_full_name(base_ty.klass_index())
            }
            _ => self.name_of_type_core(base_ty, depth + 1)?,
        };
        if class_inst_va == 0 {
            return Ok(format!("{base_name}</*TODO args*/>"));
        }
        let argc = self
            .pe
            .read_u64_va(class_inst_va)
            .context("GenericInst.argc")? as usize;
        let argv = self
            .pe
            .read_u64_va(class_inst_va + 8)
            .context("GenericInst.argv")?;
        let mut args = Vec::new();
        let n = argc.min(64);
        for i in 0..n {
            let Some(arg_ty_va) = self.pe.read_u64_va(argv + (i as u64) * 8) else {
                args.push("?".into());
                continue;
            };
            match self.read_type_at_va(arg_ty_va) {
                Ok(t) => args.push(
                    self.name_of_type(t, depth + 1)
                        .unwrap_or_else(|_| "?".into()),
                ),
                Err(_) => args.push("?".into()),
            }
        }
        if argc > n {
            args.push("/*…*/".into());
        }
        Ok(format!("{base_name}<{}>", args.join(", ")))
    }
}

pub fn sanitize_ident(name: &str) -> String {
    if name.is_empty() {
        return "_".into();
    }
    let mut out = String::with_capacity(name.len());
    for (i, c) in name.chars().enumerate() {
        let ok = c.is_ascii_alphanumeric() || c == '_' || c == '`';
        if ok {
            if i == 0 && c.is_ascii_digit() {
                out.push('_');
            }
            out.push(c);
        } else {
            out.push('_');
        }
    }
    if out.is_empty() {
        "_".into()
    } else {
        out
    }
}

pub fn sanitize_ns(ns: &str) -> String {
    ns.split('.')
        .map(sanitize_ident)
        .collect::<Vec<_>>()
        .join(".")
}

fn generic_param_label(meta: &Metadata, data: i32, method: bool) -> String {
    let idx = data.saturating_abs() as usize;
    let pos = meta
        .generic_parameters
        .get(idx)
        .map(|gp| gp.num as u32)
        .unwrap_or(idx as u32);
    if method {
        format!("TM{pos}")
    } else {
        format!("T{pos}")
    }
}

/// FieldAttributes stored in the field's Il2CppType.bits low 16 bits.
pub mod field_attrs {
    pub const FIELD_ACCESS_MASK: u32 = 0x0007;
    pub const PUBLIC: u32 = 0x0006;
    pub const STATIC: u32 = 0x0010;
    pub const LITERAL: u32 = 0x0040;
}
