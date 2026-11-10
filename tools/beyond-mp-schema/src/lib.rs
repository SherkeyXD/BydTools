//! Beyond.MemoryPack schema exporter for Endfield (ZMD).
//!
//! Reads `global-metadata.dat` plus `GameAssembly.dll` and writes the wrapper
//! `wire_order` needed to deserialize VFS JsonData MemoryPack payloads.
//! This is the schema specialist only — not a full IL2CPP inspector.

pub mod codegen;
pub mod decode;
pub mod layout;
pub mod metadata;
pub mod native;
pub mod pe;
pub mod protocol;
pub mod reg;
pub mod schema;
pub mod stringpool;
pub mod type_cs;
pub mod type_index;
pub mod typesys;
pub mod wire;

pub use decode::{decode_document, decode_schema_json, decode_wrappers, DecodeOptions};
pub use metadata::Metadata;
pub use pe::PeImage;
pub use reg::{
    locate_metadata_registration, LocateOptions, LocatedRegistration, MetadataRegistration,
};
pub use schema::{export_bundle, write_bundle, ExportBundle, SchemaDocument};
pub use stringpool::StringTable;
