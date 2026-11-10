use thiserror::Error;

#[derive(Debug, Error)]
pub enum MetadataError {
    #[error("metadata too small: need {need} bytes, got {got}")]
    TooSmall { need: usize, got: usize },

    #[error("truncated read at offset {at}")]
    Truncated { at: usize },

    #[error("bad metadata magic: got 0x{got:08X}, expected 0xFAB11BAF")]
    BadMagic { got: u32 },

    #[error("unsupported metadata version {version} (Endfield global-metadata is version 29)")]
    UnsupportedVersion { version: i32 },

    #[error(
        "unexpected hybrid header size: string_literal.offset={string_literal_offset:#x}, expected 0x110"
    )]
    UnexpectedHeaderSize { string_literal_offset: i32 },

    #[error("invalid section offset={offset} size={size}")]
    InvalidSection { offset: i32, size: i32 },

    #[error("section size {size} is not a multiple of stride {stride}")]
    BadStride { size: usize, stride: usize },

    #[error("section out of bounds: offset={offset} size={size} file_len={file_len}")]
    OutOfBounds {
        offset: i32,
        size: i32,
        file_len: usize,
    },

    #[error("string index {index} out of heap (heap_size={heap_size})")]
    StringOob { index: i32, heap_size: i32 },

    #[error("string at heap+{index} is not NUL-terminated within heap")]
    StringNotTerminated { index: i32 },

    #[error("io: {0}")]
    Io(#[from] std::io::Error),
}
