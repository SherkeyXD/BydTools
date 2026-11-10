//! Table record layouts for Endfield metadata v29.
//!
//! Only the rows the MemoryPack exporter reads are parsed. Attribute blobs,
//! field defaults, and events are intentionally not loaded.

/// `Il2CppImageDefinition` — stock modern size `0x28`.
pub const IMAGE_STRIDE: usize = 0x28;

/// Endfield `Il2CppTypeDefinition`. Stock v29 is `0x58`; HG adds 4 bytes → `0x5c`.
pub const TYPEDEF_STRIDE: usize = 0x5c;

/// Stock `Il2CppMethodDefinition` size.
pub const METHOD_STRIDE: usize = 0x20;

/// Stock `Il2CppFieldDefinition` size.
pub const FIELD_STRIDE: usize = 0x0c;

/// Stock `Il2CppParameterDefinition` (`0x0c`).
pub const PARAMETER_STRIDE: usize = 0x0c;

/// Stock `Il2CppPropertyDefinition` (`0x14`).
pub const PROPERTY_STRIDE: usize = 0x14;

/// Stock `Il2CppGenericParameter` (`0x10`).
pub const GENERIC_PARAMETER_STRIDE: usize = 0x10;

#[derive(Debug, Clone)]
#[allow(dead_code)]
pub struct ImageDefinition {
    pub name_index: i32,
    pub assembly_index: i32,
    pub type_start: i32,
    pub type_count: u32,
    pub exported_type_start: i32,
    pub exported_type_count: u32,
    pub entry_point_index: i32,
    pub token: u32,
    pub custom_attribute_start: i32,
    pub custom_attribute_count: u32,
}

impl ImageDefinition {
    pub fn parse(buf: &[u8]) -> Self {
        assert!(buf.len() >= IMAGE_STRIDE);
        let r = |o: usize| i32::from_le_bytes(buf[o..o + 4].try_into().unwrap());
        let ru = |o: usize| u32::from_le_bytes(buf[o..o + 4].try_into().unwrap());
        Self {
            name_index: r(0x00),
            assembly_index: r(0x04),
            type_start: r(0x08),
            type_count: ru(0x0c),
            exported_type_start: r(0x10),
            exported_type_count: ru(0x14),
            entry_point_index: r(0x18),
            token: ru(0x1c),
            custom_attribute_start: r(0x20),
            custom_attribute_count: ru(0x24),
        }
    }
}

/// Endfield TypeDefinition (`0x5c`).
///
/// Extra `u32` sits at `+0x40` (after `interface_offsets_start`), shifting the
/// ushort count block / bitfield / token by +4 versus stock v29.
#[derive(Debug, Clone)]
#[allow(dead_code)]
pub struct TypeDefinition {
    pub name_index: i32,
    pub namespace_index: i32,
    pub byval_type_index: i32,
    pub declaring_type_index: i32,
    pub parent_index: i32,
    pub element_type_index: i32,
    pub generic_container_index: i32,
    pub flags: u32,
    pub field_index: i32,
    pub method_index: i32,
    pub event_index: i32,
    pub property_index: i32,
    pub nested_type_index: i32,
    pub interfaces_index: i32,
    pub vtable_index: i32,
    pub interface_offsets_start: i32,
    /// Semantics unknown; often 0. Required so the stride stays `0x5c`.
    pub endfield_extra: u32,
    pub method_count: u16,
    pub property_count: u16,
    pub field_count: u16,
    pub event_count: u16,
    pub nested_type_count: u16,
    pub vtable_count: u16,
    pub interfaces_count: u16,
    pub interface_offsets_count: u16,
    pub bitfield: u32,
    pub token: u32,
}

impl TypeDefinition {
    pub fn parse(buf: &[u8]) -> Self {
        assert!(buf.len() >= TYPEDEF_STRIDE);
        let r = |o: usize| i32::from_le_bytes(buf[o..o + 4].try_into().unwrap());
        let ru = |o: usize| u32::from_le_bytes(buf[o..o + 4].try_into().unwrap());
        let ru16 = |o: usize| u16::from_le_bytes(buf[o..o + 2].try_into().unwrap());
        Self {
            name_index: r(0x00),
            namespace_index: r(0x04),
            byval_type_index: r(0x08),
            declaring_type_index: r(0x0c),
            parent_index: r(0x10),
            element_type_index: r(0x14),
            generic_container_index: r(0x18),
            flags: ru(0x1c),
            field_index: r(0x20),
            method_index: r(0x24),
            event_index: r(0x28),
            property_index: r(0x2c),
            nested_type_index: r(0x30),
            interfaces_index: r(0x34),
            vtable_index: r(0x38),
            interface_offsets_start: r(0x3c),
            endfield_extra: ru(0x40),
            method_count: ru16(0x44),
            property_count: ru16(0x46),
            field_count: ru16(0x48),
            event_count: ru16(0x4a),
            nested_type_count: ru16(0x4c),
            vtable_count: ru16(0x4e),
            interfaces_count: ru16(0x50),
            interface_offsets_count: ru16(0x52),
            bitfield: ru(0x54),
            token: ru(0x58),
        }
    }
}

#[derive(Debug, Clone)]
#[allow(dead_code)]
pub struct MethodDefinition {
    pub name_index: i32,
    pub declaring_type: i32,
    pub return_type: i32,
    pub parameter_start: i32,
    pub generic_container_index: i32,
    pub token: u32,
    pub flags: u16,
    pub impl_flags: u16,
    pub slot: u16,
    pub parameter_count: u16,
}

impl MethodDefinition {
    pub fn parse(buf: &[u8]) -> Self {
        assert!(buf.len() >= METHOD_STRIDE);
        let r = |o: usize| i32::from_le_bytes(buf[o..o + 4].try_into().unwrap());
        let ru = |o: usize| u32::from_le_bytes(buf[o..o + 4].try_into().unwrap());
        let ru16 = |o: usize| u16::from_le_bytes(buf[o..o + 2].try_into().unwrap());
        Self {
            name_index: r(0x00),
            declaring_type: r(0x04),
            return_type: r(0x08),
            parameter_start: r(0x0c),
            generic_container_index: r(0x10),
            token: ru(0x14),
            flags: ru16(0x18),
            impl_flags: ru16(0x1a),
            slot: ru16(0x1c),
            parameter_count: ru16(0x1e),
        }
    }
}

#[derive(Debug, Clone)]
#[allow(dead_code)]
pub struct FieldDefinition {
    pub name_index: i32,
    pub type_index: i32,
    pub token: u32,
}

impl FieldDefinition {
    pub fn parse(buf: &[u8]) -> Self {
        assert!(buf.len() >= FIELD_STRIDE);
        Self {
            name_index: i32::from_le_bytes(buf[0..4].try_into().unwrap()),
            type_index: i32::from_le_bytes(buf[4..8].try_into().unwrap()),
            token: u32::from_le_bytes(buf[8..12].try_into().unwrap()),
        }
    }
}

#[derive(Debug, Clone)]
#[allow(dead_code)]
pub struct ParameterDefinition {
    pub name_index: i32,
    pub token: u32,
    pub type_index: i32,
}

impl ParameterDefinition {
    pub fn parse(buf: &[u8]) -> Self {
        assert!(buf.len() >= PARAMETER_STRIDE);
        Self {
            name_index: i32::from_le_bytes(buf[0..4].try_into().unwrap()),
            token: u32::from_le_bytes(buf[4..8].try_into().unwrap()),
            type_index: i32::from_le_bytes(buf[8..12].try_into().unwrap()),
        }
    }
}

#[derive(Debug, Clone)]
#[allow(dead_code)]
pub struct PropertyDefinition {
    pub name_index: i32,
    pub get: i32,
    pub set: i32,
    pub attrs: u32,
    pub token: u32,
}

impl PropertyDefinition {
    pub fn parse(buf: &[u8]) -> Self {
        assert!(buf.len() >= PROPERTY_STRIDE);
        let r = |o: usize| i32::from_le_bytes(buf[o..o + 4].try_into().unwrap());
        let ru = |o: usize| u32::from_le_bytes(buf[o..o + 4].try_into().unwrap());
        Self {
            name_index: r(0x00),
            get: r(0x04),
            set: r(0x08),
            attrs: ru(0x0c),
            token: ru(0x10),
        }
    }
}

#[derive(Debug, Clone, Copy)]
#[allow(dead_code)]
pub struct GenericParameter {
    pub owner_index: i32,
    pub name_index: i32,
    pub constraints_start: i16,
    pub constraints_count: i16,
    pub num: u16,
    pub flags: u16,
}

impl GenericParameter {
    pub fn parse(buf: &[u8]) -> Self {
        assert!(buf.len() >= GENERIC_PARAMETER_STRIDE);
        Self {
            owner_index: i32::from_le_bytes(buf[0..4].try_into().unwrap()),
            name_index: i32::from_le_bytes(buf[4..8].try_into().unwrap()),
            constraints_start: i16::from_le_bytes(buf[8..10].try_into().unwrap()),
            constraints_count: i16::from_le_bytes(buf[10..12].try_into().unwrap()),
            num: u16::from_le_bytes(buf[12..14].try_into().unwrap()),
            flags: u16::from_le_bytes(buf[14..16].try_into().unwrap()),
        }
    }
}
