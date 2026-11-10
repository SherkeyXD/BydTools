//! Experimental sample reader for a single simple MemoryPack payload.
//!
//! Not the product. BydTools (or another consumer of `schemas.json` `wire_order`)
//! owns JsonData deserialization, including polymorphic formatters. This module
//! only walks object headers, primitives, pool ids, lists, dictionaries, and
//! nested wrappers whose `memberCount` matches `wire_order`.

use crate::schema::{SchemaDocument, WrapperSchema};
use crate::stringpool::StringTable;
use crate::wire::{self, basename_of, WireKind};
use anyhow::{bail, Context, Result};
use serde::Deserialize;
use serde_json::{Map, Number, Value};
use std::collections::HashMap;

#[derive(Debug, Deserialize)]
struct SchemaView {
    wrappers: Vec<WrapperSchema>,
}

pub struct DecodeOptions<'a> {
    pub type_name: &'a str,
    pub wrapper: Option<&'a str>,
    pub strings: &'a StringTable,
}

pub fn decode_schema_json(
    schemas_json: &Value,
    bytes: &[u8],
    opts: &DecodeOptions<'_>,
) -> Result<Value> {
    let view: SchemaView = serde_json::from_value(schemas_json.clone())
        .context("schemas JSON is missing a wrappers array")?;
    decode_wrappers(&view.wrappers, bytes, opts)
}

pub fn decode_document(
    doc: &SchemaDocument,
    bytes: &[u8],
    opts: &DecodeOptions<'_>,
) -> Result<Value> {
    decode_wrappers(&doc.wrappers, bytes, opts)
}

pub fn decode_wrappers(
    wrappers: &[WrapperSchema],
    bytes: &[u8],
    opts: &DecodeOptions<'_>,
) -> Result<Value> {
    if bytes.is_empty() {
        bail!("input is empty");
    }
    if looks_like_json_text(bytes) {
        bail!(
            "input looks like UTF-8 JSON, not a MemoryPack payload. \
JsonData stores both; only non-JSON files use these schemas."
        );
    }
    let catalog = Catalog::new(wrappers);
    let wrapper = catalog.resolve(opts.type_name, opts.wrapper)?;
    let mut reader = Reader {
        data: bytes,
        pos: 0,
        catalog: &catalog,
        strings: opts.strings,
        depth: 0,
    };
    let value = reader.read_object(&wrapper)?;
    if reader.pos != reader.data.len() {
        bail!(
            "trailing {} byte(s) after {} (decoded {} of {})",
            reader.data.len() - reader.pos,
            wrapper.wrapper_type,
            reader.pos,
            reader.data.len()
        );
    }
    Ok(value)
}

pub fn looks_like_json_text(bytes: &[u8]) -> bool {
    matches!(
        bytes.iter().find(|b| !b.is_ascii_whitespace()).copied(),
        Some(b'{') | Some(b'[')
    )
}

struct Catalog<'a> {
    wrappers: &'a [WrapperSchema],
    by_target: HashMap<&'a str, Vec<usize>>,
    by_wrapper: HashMap<&'a str, usize>,
}

impl<'a> Catalog<'a> {
    fn new(wrappers: &'a [WrapperSchema]) -> Self {
        let mut by_target: HashMap<&str, Vec<usize>> = HashMap::new();
        let mut by_wrapper = HashMap::new();
        for (i, w) in wrappers.iter().enumerate() {
            by_wrapper.insert(w.wrapper_type.as_str(), i);
            if let Some(t) = w.target_type.as_deref() {
                by_target.entry(t).or_default().push(i);
            }
        }
        Self {
            wrappers,
            by_target,
            by_wrapper,
        }
    }

    fn has_target(&self, ty: &str) -> bool {
        self.by_target.contains_key(ty)
    }

    fn resolve(&self, type_name: &str, wrapper: Option<&str>) -> Result<WrapperSchema> {
        if let Some(wname) = wrapper {
            let idx = *self
                .by_wrapper
                .get(wname)
                .with_context(|| format!("no wrapper_type {wname}"))?;
            let w = &self.wrappers[idx];
            if !type_name.is_empty()
                && type_name != w.wrapper_type
                && w.target_type.as_deref() != Some(type_name)
                && basename_of(w.target_type.as_deref().unwrap_or("")) != type_name
                && basename_of(&w.wrapper_type) != type_name
            {
                bail!(
                    "--type {type_name} does not match --wrapper {} (target {:?})",
                    w.wrapper_type,
                    w.target_type
                );
            }
            return Ok(w.clone());
        }
        if let Some(idxs) = self.by_target.get(type_name) {
            return self.only(idxs, type_name);
        }
        if let Some(idx) = self.by_wrapper.get(type_name) {
            return Ok(self.wrappers[*idx].clone());
        }
        let base_hits: Vec<usize> = self
            .wrappers
            .iter()
            .enumerate()
            .filter(|(_, w)| {
                w.target_type
                    .as_deref()
                    .is_some_and(|t| basename_of(t) == type_name)
                    || basename_of(&w.wrapper_type) == type_name
            })
            .map(|(i, _)| i)
            .collect();
        if base_hits.is_empty() {
            bail!("no schema for type {type_name}");
        }
        self.only(&base_hits, type_name)
    }

    fn only(&self, idxs: &[usize], label: &str) -> Result<WrapperSchema> {
        if idxs.len() == 1 {
            return Ok(self.wrappers[idxs[0]].clone());
        }
        let names: Vec<&str> = idxs
            .iter()
            .map(|&i| self.wrappers[i].wrapper_type.as_str())
            .collect();
        bail!(
            "type {label} matches {} wrappers; pass --wrapper. candidates: {}",
            names.len(),
            names.join(", ")
        )
    }

    fn object_for(&self, ty: &str) -> Result<WrapperSchema> {
        let idxs = self
            .by_target
            .get(ty)
            .with_context(|| format!("no wrapper for nested type {ty}"))?;
        self.only(idxs, ty)
    }
}

struct Reader<'a> {
    data: &'a [u8],
    pos: usize,
    catalog: &'a Catalog<'a>,
    strings: &'a StringTable,
    depth: u32,
}

impl Reader<'_> {
    fn read_object(&mut self, wrapper: &WrapperSchema) -> Result<Value> {
        if self.depth > 64 {
            bail!("nesting deeper than 64 at {}", wrapper.wrapper_type);
        }
        self.depth += 1;
        let count = self
            .read_member_count()
            .with_context(|| format!("object header of {}", wrapper.wrapper_type))?;
        let value = match count {
            None => Value::Null,
            Some(n) if n == wrapper.wire_order.len() => {
                let mut obj = Map::new();
                for member in &wrapper.wire_order {
                    let v = self.read_value(&member.ty).with_context(|| {
                        format!("{}.{} ({})", wrapper.wrapper_type, member.name, member.ty)
                    })?;
                    obj.insert(member.name.clone(), v);
                }
                Value::Object(obj)
            }
            Some(n) => {
                self.depth -= 1;
                bail!(
                    "{} memberCount {n} != wire_order length {}",
                    wrapper.wrapper_type,
                    wrapper.wire_order.len()
                );
            }
        };
        self.depth -= 1;
        Ok(value)
    }

    fn read_value(&mut self, ty: &str) -> Result<Value> {
        match wire::classify(ty) {
            WireKind::Bool => Ok(Value::Bool(self.u8()? != 0)),
            WireKind::U8 => Ok(Value::Number((self.u8()? as u64).into())),
            WireKind::I8 => Ok(Value::Number((self.u8()? as i8 as i64).into())),
            WireKind::U16 => Ok(Value::Number((self.u16()? as u64).into())),
            WireKind::I16 => Ok(Value::Number((self.u16()? as i16 as i64).into())),
            WireKind::U32 => Ok(Value::Number((self.u32()? as u64).into())),
            WireKind::I32 => Ok(Value::Number((self.i32()? as i64).into())),
            WireKind::U64 => Ok(num_u64(self.u64()?)),
            WireKind::I64 => Ok(Value::Number(self.i64()?.into())),
            WireKind::F32 => Ok(num_f64(self.f32()? as f64)),
            WireKind::F64 => Ok(num_f64(self.f64()?)),
            WireKind::Char => Ok(Value::String(char_from_u16(self.u16()?))),
            WireKind::String => self.read_pool_string(),
            WireKind::Dictionary { key, value } => self.read_dict(&key, &value),
            WireKind::List { elem } => self.read_list(&elem),
            WireKind::Nullable { inner } => {
                let flag = self.u8()?;
                if flag == 0 {
                    Ok(Value::Null)
                } else {
                    self.read_value(&inner)
                }
            }
            WireKind::Vector2 => self.read_floats(2),
            WireKind::Vector3 => self.read_floats(3),
            WireKind::Vector4 | WireKind::Quaternion | WireKind::Color => self.read_floats(4),
            WireKind::Color32 => Ok(Value::Array(
                (0..4)
                    .map(|_| self.u8().map(|b| Value::Number((b as u64).into())))
                    .collect::<Result<Vec<_>>>()?,
            )),
            WireKind::Vector2Int => self.read_ints(2),
            WireKind::Vector3Int => self.read_ints(3),
            WireKind::Named(name) => {
                if name == "object" || name == "System.Object" {
                    bail!("untyped object has no Beyond.MemoryPack wire_order");
                }
                if self.catalog.has_target(&name) {
                    let w = self.catalog.object_for(&name)?;
                    self.read_object(&w)
                } else {
                    // Enums are not wrapped. Beyond stores the usual int32 underlying value.
                    Ok(Value::Number((self.i32()? as i64).into()))
                }
            }
        }
    }

    fn read_pool_string(&mut self) -> Result<Value> {
        let id = self.i32()?;
        match id {
            -1 => Ok(Value::String(String::new())),
            -2 => Ok(Value::Null),
            other => {
                if let Some(s) = self.strings.get(other) {
                    Ok(Value::String(s.to_string()))
                } else {
                    Ok(serde_json::json!({ "$stringPool": other }))
                }
            }
        }
    }

    fn read_list(&mut self, elem: &str) -> Result<Value> {
        let Some(n) = self.read_count()? else {
            return Ok(Value::Null);
        };
        let mut arr = Vec::with_capacity(n);
        for i in 0..n {
            arr.push(
                self.read_value(elem)
                    .with_context(|| format!("list[{i}] of {elem}"))?,
            );
        }
        Ok(Value::Array(arr))
    }

    fn read_dict(&mut self, key_ty: &str, val_ty: &str) -> Result<Value> {
        let Some(n) = self.read_count()? else {
            return Ok(Value::Null);
        };
        let mut obj = Map::new();
        for i in 0..n {
            let key_val = self
                .read_value(key_ty)
                .with_context(|| format!("dict key[{i}] {key_ty}"))?;
            let key = json_key(&key_val)?;
            let val = self
                .read_value(val_ty)
                .with_context(|| format!("dict[{key}] {val_ty}"))?;
            obj.insert(key, val);
        }
        Ok(Value::Object(obj))
    }

    fn read_floats(&mut self, n: usize) -> Result<Value> {
        let mut arr = Vec::with_capacity(n);
        for _ in 0..n {
            arr.push(num_f64(self.f32()? as f64));
        }
        Ok(Value::Array(arr))
    }

    fn read_ints(&mut self, n: usize) -> Result<Value> {
        let mut arr = Vec::with_capacity(n);
        for _ in 0..n {
            arr.push(Value::Number((self.i32()? as i64).into()));
        }
        Ok(Value::Array(arr))
    }

    fn read_member_count(&mut self) -> Result<Option<usize>> {
        let b = self.u8()?;
        if b == 255 {
            return Ok(None);
        }
        if b == 250 {
            let n = self.i32()?;
            if n < 0 {
                bail!("negative wide memberCount {n}");
            }
            return Ok(Some(n as usize));
        }
        Ok(Some(b as usize))
    }

    fn read_count(&mut self) -> Result<Option<usize>> {
        let n = self.i32()?;
        if n == -1 {
            return Ok(None);
        }
        if n < 0 {
            bail!("unexpected collection length {n}");
        }
        if n > 5_000_000 {
            bail!("collection length {n} is unreasonably large");
        }
        Ok(Some(n as usize))
    }

    fn u8(&mut self) -> Result<u8> {
        Ok(self.take(1)?[0])
    }

    fn u16(&mut self) -> Result<u16> {
        Ok(u16::from_le_bytes(self.take(2)?.try_into().unwrap()))
    }

    fn u32(&mut self) -> Result<u32> {
        Ok(u32::from_le_bytes(self.take(4)?.try_into().unwrap()))
    }

    fn i32(&mut self) -> Result<i32> {
        Ok(self.u32()? as i32)
    }

    fn u64(&mut self) -> Result<u64> {
        Ok(u64::from_le_bytes(self.take(8)?.try_into().unwrap()))
    }

    fn i64(&mut self) -> Result<i64> {
        Ok(self.u64()? as i64)
    }

    fn f32(&mut self) -> Result<f32> {
        Ok(f32::from_le_bytes(self.take(4)?.try_into().unwrap()))
    }

    fn f64(&mut self) -> Result<f64> {
        Ok(f64::from_le_bytes(self.take(8)?.try_into().unwrap()))
    }

    fn take(&mut self, n: usize) -> Result<&[u8]> {
        if self.pos + n > self.data.len() {
            bail!(
                "truncated at byte {} (need {n}, len {})",
                self.pos,
                self.data.len()
            );
        }
        let start = self.pos;
        self.pos += n;
        Ok(&self.data[start..self.pos])
    }
}

fn json_key(v: &Value) -> Result<String> {
    match v {
        Value::String(s) => Ok(s.clone()),
        Value::Number(n) => Ok(n.to_string()),
        Value::Bool(b) => Ok(b.to_string()),
        Value::Null => Ok("null".into()),
        Value::Object(o) => {
            if let Some(id) = o.get("$stringPool").and_then(|x| x.as_i64()) {
                return Ok(format!("#{id}"));
            }
            bail!("dictionary key is not a scalar: {v}")
        }
        Value::Array(_) => bail!("dictionary key is an array"),
    }
}

fn num_u64(v: u64) -> Value {
    Value::Number(v.into())
}

fn num_f64(v: f64) -> Value {
    Number::from_f64(v)
        .map(Value::Number)
        .unwrap_or_else(|| Value::String(v.to_string()))
}

fn char_from_u16(v: u16) -> String {
    char::from_u32(v as u32)
        .map(|c| c.to_string())
        .unwrap_or_else(|| format!("\\u{v:04X}"))
}
