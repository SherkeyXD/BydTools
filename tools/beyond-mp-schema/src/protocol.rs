//! Self-describing Beyond.MemoryPack read protocol.
//!
//! Addresses are verified against this GameAssembly; they are evidence, not
//! a decoder lookup table. The operations themselves are the contract.

use crate::pe::PeImage;
use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ProtocolOp {
    pub id: String,
    pub operation: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub va: Option<String>,
    pub status: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ProtocolDocument {
    pub format: String,
    pub generated_by: String,
    pub ops: Vec<ProtocolOp>,
    pub string_pool: StringPoolProtocol,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct StringPoolProtocol {
    pub magic: String,
    pub version: u32,
    pub encoding: String,
    pub empty: i32,
    pub null: i32,
    pub note: String,
}

const HELPERS: &[(&str, &str, u64)] = &[
    ("object-header", "u8 count; 255 null; 250 then i32 wide count", 0x18C8_C5028),
    ("collection-header", "i32le length; -1 null", 0x18C8_C4FC0),
    ("union-header", "u8 tag if < 0xFA; 0xFA then u16le extended tag; 0xFB..0xFF fail in this helper", 0x18C8_C5074),
    ("bool", "u8; nonzero is true", 0x18C8_C3A6C),
    ("beyond-string", "if pool present: i32 id, -2 null, -1 empty slot, else inline string", 0x1851_F0F70),
    (
        "dictionary-v2",
        "mandatory u8 container header; 255 null, any other value is present; then collection-header and key/value pairs. 0 is present, not null",
        0x185C_EA340,
    ),
];

pub fn build_protocol(pe: &PeImage) -> ProtocolDocument {
    let ops = HELPERS
        .iter()
        .map(|(id, op, va)| {
            let mapped = pe.va_mapped(*va);
            ProtocolOp {
                id: (*id).into(),
                operation: (*op).into(),
                va: Some(format!("{va:#x}")),
                status: if mapped {
                    "confirmed_mapped".into()
                } else {
                    "unmapped_in_this_image".into()
                },
            }
        })
        .collect();
    ProtocolDocument {
        format: "beyond-mp-protocol/1".into(),
        generated_by: "beyond-mp-schema".into(),
        ops,
        string_pool: StringPoolProtocol {
            magic: "USPC".into(),
            version: 1,
            encoding: "block of (i32 byte_length + utf8)* reached via offset table; flags bit0 = LZ4".into(),
            empty: -1,
            null: -2,
            note: "StringPool bytes are game data, not part of this schema. Global id = blockIndex * blockSize + localIndex.".into(),
        },
    }
}
