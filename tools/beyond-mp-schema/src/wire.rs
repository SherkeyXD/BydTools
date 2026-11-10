//! Classify a C# type string from `schemas.json` into a MemoryPack wire kind.
//!
//! Beyond string values are pool ids, not inline UTF-8. Collections use the
//! standard MemoryPack length header (`int32`, `-1` = null).

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum WireKind {
    Bool,
    U8,
    I8,
    U16,
    I16,
    U32,
    I32,
    U64,
    I64,
    F32,
    F64,
    Char,
    String,
    Dictionary {
        key: String,
        value: String,
    },
    List {
        elem: String,
    },
    Nullable {
        inner: String,
    },
    Vector2,
    Vector3,
    Vector4,
    Quaternion,
    Color,
    Color32,
    Vector2Int,
    Vector3Int,
    /// Named type. The caller decides object-vs-enum by wrapper lookup.
    Named(String),
}

impl WireKind {
    pub fn label(&self) -> &'static str {
        match self {
            Self::Bool => "bool",
            Self::U8 => "uint8",
            Self::I8 => "int8",
            Self::U16 => "uint16",
            Self::I16 => "int16",
            Self::U32 => "uint32",
            Self::I32 => "int32",
            Self::U64 => "uint64",
            Self::I64 => "int64",
            Self::F32 => "float32",
            Self::F64 => "float64",
            Self::Char => "char",
            Self::String => "string",
            Self::Dictionary { .. } => "dictionary",
            Self::List { .. } => "list",
            Self::Nullable { .. } => "nullable",
            Self::Vector2 => "vector2",
            Self::Vector3 => "vector3",
            Self::Vector4 => "vector4",
            Self::Quaternion => "quaternion",
            Self::Color => "color",
            Self::Color32 => "color32",
            Self::Vector2Int => "vector2int",
            Self::Vector3Int => "vector3int",
            Self::Named(_) => "named",
        }
    }
}

pub fn classify(ty: &str) -> WireKind {
    let mut t = ty.trim();
    if let Some(stripped) = t.strip_suffix('&') {
        t = stripped.trim();
    }
    match t {
        "bool" | "System.Boolean" => return WireKind::Bool,
        "byte" | "System.Byte" => return WireKind::U8,
        "sbyte" | "System.SByte" => return WireKind::I8,
        "short" | "System.Int16" => return WireKind::I16,
        "ushort" | "System.UInt16" => return WireKind::U16,
        "int" | "System.Int32" => return WireKind::I32,
        "uint" | "System.UInt32" => return WireKind::U32,
        "long" | "System.Int64" => return WireKind::I64,
        "ulong" | "System.UInt64" => return WireKind::U64,
        "float" | "System.Single" => return WireKind::F32,
        "double" | "System.Double" => return WireKind::F64,
        "char" | "System.Char" => return WireKind::Char,
        "string" | "System.String" => return WireKind::String,
        "UnityEngine.Vector2" => return WireKind::Vector2,
        "UnityEngine.Vector3" => return WireKind::Vector3,
        "UnityEngine.Vector4" => return WireKind::Vector4,
        "UnityEngine.Quaternion" => return WireKind::Quaternion,
        "UnityEngine.Color" => return WireKind::Color,
        "UnityEngine.Color32" => return WireKind::Color32,
        "UnityEngine.Vector2Int" => return WireKind::Vector2Int,
        "UnityEngine.Vector3Int" => return WireKind::Vector3Int,
        _ => {}
    }
    if let Some(elem) = t.strip_suffix("[]") {
        return WireKind::List {
            elem: elem.trim().to_string(),
        };
    }
    if let Some((head, args)) = split_generic(t) {
        let leaf = head.rsplit('.').next().unwrap_or(head);
        if args.len() == 2
            && (leaf.starts_with("Dictionary`")
                || leaf.starts_with("IDictionary`")
                || leaf.starts_with("IReadOnlyDictionary`"))
        {
            return WireKind::Dictionary {
                key: args[0].clone(),
                value: args[1].clone(),
            };
        }
        if args.len() == 1
            && (leaf.starts_with("List`")
                || leaf.starts_with("IList`")
                || leaf.starts_with("IReadOnlyList`")
                || leaf.starts_with("ICollection`")
                || leaf.starts_with("IReadOnlyCollection`")
                || leaf.starts_with("HashSet`")
                || leaf.starts_with("Queue`")
                || leaf.starts_with("Stack`"))
        {
            return WireKind::List {
                elem: args[0].clone(),
            };
        }
        if leaf.starts_with("Nullable`") && args.len() == 1 {
            return WireKind::Nullable {
                inner: args[0].clone(),
            };
        }
    }
    WireKind::Named(t.to_string())
}

pub fn split_generic(ty: &str) -> Option<(&str, Vec<String>)> {
    let open = ty.find('<')?;
    if !ty.ends_with('>') {
        return None;
    }
    let head = &ty[..open];
    let inner = &ty[open + 1..ty.len() - 1];
    Some((head, split_top_level(inner)))
}

pub fn split_top_level(s: &str) -> Vec<String> {
    let mut out = Vec::new();
    let mut depth = 0i32;
    let mut start = 0usize;
    for (i, c) in s.char_indices() {
        match c {
            '<' => depth += 1,
            '>' => depth -= 1,
            ',' if depth == 0 => {
                out.push(s[start..i].trim().to_string());
                start = i + 1;
            }
            _ => {}
        }
    }
    let tail = s[start..].trim();
    if !tail.is_empty() {
        out.push(tail.to_string());
    }
    out
}

pub fn basename_of(type_name: &str) -> String {
    let head = type_name.split('<').next().unwrap_or(type_name);
    head.rsplit('.').next().unwrap_or(head).to_string()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn classifies_bamboo_types() {
        let d = classify(
            "System.Collections.Generic.Dictionary`2<ulong, Beyond.Gameplay.BambooRaftTaskData>",
        );
        assert_eq!(
            d,
            WireKind::Dictionary {
                key: "ulong".into(),
                value: "Beyond.Gameplay.BambooRaftTaskData".into(),
            }
        );
        assert_eq!(
            classify("System.Collections.Generic.List`1<UnityEngine.Vector2>"),
            WireKind::List {
                elem: "UnityEngine.Vector2".into(),
            }
        );
        assert_eq!(classify("string"), WireKind::String);
        assert_eq!(classify("QuestState"), WireKind::Named("QuestState".into()));
        assert_eq!(
            basename_of("Beyond.Gameplay.BambooRaftTaskTable"),
            "BambooRaftTaskTable"
        );
    }
}
