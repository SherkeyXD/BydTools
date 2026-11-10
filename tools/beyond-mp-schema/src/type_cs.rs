//! Compact C# view of MemoryPack wrappers.
//!
//! Field order is `wire_order`. This is a dump for BydTools, not a project
//! that compiles: nested types often share the leaf name `Data`, and generic
//! arity markers are rewritten only for readability.

use crate::schema::{SchemaDocument, WrapperSchema};
use std::fmt::Write as _;

pub fn render_type_cs(doc: &SchemaDocument) -> String {
    let mut out = String::with_capacity(doc.wrappers.len().saturating_mul(240));
    let _ = writeln!(
        out,
        r#"// beyond-mp-schema type dump
// Not a buildable assembly and not an il2cpp.cs inspector stub.
//
// Each class is one *ForMemoryPack wrapper. Declared field order IS the
// MemoryPack wire order (native Deserialize setter order). Do not reorder
// to match the target type's instance fields; see orders_differ.
//
// string fields are Beyond StringPool int32 ids (-1 empty, -2 null), not UTF-8.
// Dictionary`2 / List`1 in schemas.json are shown here as Dictionary<> / List<>.
//
// wrappers = {wrappers}
// IMemoryPackSerialize markers = {markers}
// orders_differ = {differ}

namespace Beyond.MemoryPack.SchemaDump
{{
"#,
        wrappers = doc.counts.wrappers,
        markers = doc.counts.serialize_markers,
        differ = doc.counts.wrappers_orders_differ_from_target_fields,
    );

    let _ = writeln!(
        out,
        "    // --- IMemoryPackSerialize markers (JsonData roots) ---"
    );
    for marker in &doc.serialize_markers {
        let _ = writeln!(
            out,
            "    // {ty}  fields={n}  wrapper={wrap}",
            ty = marker.type_name,
            n = marker.instance_field_count,
            wrap = marker.matching_wrapper.as_deref().unwrap_or("-"),
        );
    }
    out.push('\n');

    for wrapper in &doc.wrappers {
        push_wrapper(&mut out, wrapper);
    }
    out.push_str("}\n");
    out
}

fn push_wrapper(out: &mut String, wrapper: &WrapperSchema) {
    let target = wrapper.target_type.as_deref().unwrap_or("-");
    let _ = writeln!(out, "    // target: {target}");
    let _ = writeln!(out, "    // wrapper: {}", wrapper.wrapper_type);
    if let Some(fmt) = &wrapper.formatter_type {
        let _ = writeln!(out, "    // formatter: {fmt}");
    }
    let _ = writeln!(
        out,
        "    // orders_differ: {}  deserialize: {}  register: {}",
        wrapper.orders_differ, wrapper.has_deserialize, wrapper.has_register_formatter
    );
    let type_name = cs_ident(wrapper_leaf(&wrapper.wrapper_type));
    let _ = writeln!(out, "    public class {type_name}");
    let _ = writeln!(out, "    {{");
    if wrapper.wire_order.is_empty() {
        let _ = writeln!(
            out,
            "        // (no __field__ properties; wire_order is empty)"
        );
    }
    for (index, member) in wrapper.wire_order.iter().enumerate() {
        let _ = writeln!(
            out,
            "        public {ty} {name}; // wire {index}  raw {raw}",
            ty = cs_type(&member.ty),
            name = cs_ident(&member.name),
            raw = member.raw_property,
        );
    }
    let _ = writeln!(out, "    }}\n");
}

fn wrapper_leaf(wrapper_type: &str) -> &str {
    wrapper_type.rsplit('.').next().unwrap_or(wrapper_type)
}

fn cs_type(ty: &str) -> String {
    let mut out = String::with_capacity(ty.len());
    let chars: Vec<char> = ty.chars().collect();
    let mut i = 0;
    while i < chars.len() {
        if chars[i] == '`' {
            i += 1;
            while i < chars.len() && chars[i].is_ascii_digit() {
                i += 1;
            }
            continue;
        }
        out.push(chars[i]);
        i += 1;
    }
    out
}

fn cs_ident(name: &str) -> String {
    if is_cs_keyword(name) {
        format!("@{name}")
    } else {
        name.to_string()
    }
}

fn is_cs_keyword(name: &str) -> bool {
    matches!(
        name,
        "abstract"
            | "as"
            | "base"
            | "bool"
            | "break"
            | "byte"
            | "case"
            | "catch"
            | "char"
            | "checked"
            | "class"
            | "const"
            | "continue"
            | "decimal"
            | "default"
            | "delegate"
            | "do"
            | "double"
            | "else"
            | "enum"
            | "event"
            | "explicit"
            | "extern"
            | "false"
            | "finally"
            | "fixed"
            | "float"
            | "for"
            | "foreach"
            | "goto"
            | "if"
            | "implicit"
            | "in"
            | "int"
            | "interface"
            | "internal"
            | "is"
            | "lock"
            | "long"
            | "namespace"
            | "new"
            | "null"
            | "object"
            | "operator"
            | "out"
            | "override"
            | "params"
            | "private"
            | "protected"
            | "public"
            | "readonly"
            | "ref"
            | "return"
            | "sbyte"
            | "sealed"
            | "short"
            | "sizeof"
            | "stackalloc"
            | "static"
            | "string"
            | "struct"
            | "switch"
            | "this"
            | "throw"
            | "true"
            | "try"
            | "typeof"
            | "uint"
            | "ulong"
            | "unchecked"
            | "unsafe"
            | "ushort"
            | "using"
            | "virtual"
            | "void"
            | "volatile"
            | "while"
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn strips_generic_arity_and_keeps_wire_order() {
        assert_eq!(
            cs_type("System.Collections.Generic.Dictionary`2<ulong, Beyond.Gameplay.BambooRaftTaskData>"),
            "System.Collections.Generic.Dictionary<ulong, Beyond.Gameplay.BambooRaftTaskData>"
        );
        assert_eq!(cs_ident("event"), "@event");
    }
}
