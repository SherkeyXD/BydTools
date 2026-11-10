//! Endfield `global-metadata.dat` reader (v29 hybrid header).
//!
//! Loads only the tables needed to name `*ForMemoryPack` wrappers, their
//! properties, target fields, and nested formatters.

mod error;
mod header;
mod types;

pub use error::MetadataError;
pub use header::{MetadataHeader, Section, ENDFIELD_HEADER_SIZE, ENDFIELD_VERSION, METADATA_MAGIC};
pub use types::{
    FieldDefinition, GenericParameter, ImageDefinition, MethodDefinition, ParameterDefinition,
    PropertyDefinition, TypeDefinition, FIELD_STRIDE, GENERIC_PARAMETER_STRIDE, IMAGE_STRIDE,
    METHOD_STRIDE, PARAMETER_STRIDE, PROPERTY_STRIDE, TYPEDEF_STRIDE,
};

use std::path::Path;

/// Loaded Endfield metadata.
pub struct Metadata {
    pub data: Vec<u8>,
    pub header: MetadataHeader,
    pub images: Vec<ImageDefinition>,
    pub type_defs: Vec<TypeDefinition>,
    pub methods: Vec<MethodDefinition>,
    pub fields: Vec<FieldDefinition>,
    pub parameters: Vec<ParameterDefinition>,
    pub properties: Vec<PropertyDefinition>,
    /// NestedTypes table: TypeDefinitionIndex entries.
    pub nested_types: Vec<i32>,
    /// Interfaces table: TypeIndex entries (into MetadataRegistration.types).
    pub interfaces: Vec<i32>,
    pub generic_parameters: Vec<GenericParameter>,
}

impl Metadata {
    pub fn from_path(path: impl AsRef<Path>) -> Result<Self, MetadataError> {
        let data = std::fs::read(path)?;
        Self::parse(data)
    }

    pub fn parse(data: Vec<u8>) -> Result<Self, MetadataError> {
        let header = MetadataHeader::parse(&data)?;
        for sec in [
            &header.string,
            &header.images,
            &header.type_definitions,
            &header.methods,
            &header.fields,
            &header.parameters,
            &header.properties,
            &header.nested_types,
            &header.interfaces,
            &header.generic_parameters,
        ] {
            Self::check_section(&data, sec)?;
        }

        let nested_types = Self::read_i32_table(&data, &header.nested_types)?;
        let interfaces = Self::read_i32_table(&data, &header.interfaces)?;

        Ok(Self {
            images: Self::read_table(&data, &header.images, IMAGE_STRIDE, ImageDefinition::parse)?,
            type_defs: Self::read_table(
                &data,
                &header.type_definitions,
                TYPEDEF_STRIDE,
                TypeDefinition::parse,
            )?,
            methods: Self::read_table(
                &data,
                &header.methods,
                METHOD_STRIDE,
                MethodDefinition::parse,
            )?,
            fields: Self::read_table(&data, &header.fields, FIELD_STRIDE, FieldDefinition::parse)?,
            parameters: Self::read_table(
                &data,
                &header.parameters,
                PARAMETER_STRIDE,
                ParameterDefinition::parse,
            )?,
            properties: Self::read_table(
                &data,
                &header.properties,
                PROPERTY_STRIDE,
                PropertyDefinition::parse,
            )?,
            nested_types,
            interfaces,
            generic_parameters: Self::read_table(
                &data,
                &header.generic_parameters,
                GENERIC_PARAMETER_STRIDE,
                GenericParameter::parse,
            )?,
            data,
            header,
        })
    }

    fn check_section(data: &[u8], sec: &Section) -> Result<(), MetadataError> {
        if sec.size == 0 {
            return Ok(());
        }
        let start = sec.offset as i64;
        let end = sec.end();
        if start < 0 || end < 0 || end as usize > data.len() {
            return Err(MetadataError::OutOfBounds {
                offset: sec.offset,
                size: sec.size,
                file_len: data.len(),
            });
        }
        Ok(())
    }

    fn read_table<T, F>(
        data: &[u8],
        sec: &Section,
        stride: usize,
        parse: F,
    ) -> Result<Vec<T>, MetadataError>
    where
        F: Fn(&[u8]) -> T,
    {
        let count = sec.count_of(stride)?;
        let base = sec.offset as usize;
        let mut out = Vec::with_capacity(count);
        for i in 0..count {
            let start = base + i * stride;
            out.push(parse(&data[start..start + stride]));
        }
        Ok(out)
    }

    fn read_i32_table(data: &[u8], sec: &Section) -> Result<Vec<i32>, MetadataError> {
        let count = sec.count_of(4)?;
        let base = sec.offset as usize;
        let mut out = Vec::with_capacity(count);
        for i in 0..count {
            let s = base + i * 4;
            out.push(i32::from_le_bytes(data[s..s + 4].try_into().unwrap()));
        }
        Ok(out)
    }

    pub fn string(&self, index: i32) -> Result<String, MetadataError> {
        if index < 0 || index >= self.header.string.size {
            return Err(MetadataError::StringOob {
                index,
                heap_size: self.header.string.size,
            });
        }
        let base = self.header.string.offset as usize + index as usize;
        let end_limit = self.header.string.offset as usize + self.header.string.size as usize;
        let slice = &self.data[base..end_limit];
        let nul = slice
            .iter()
            .position(|&b| b == 0)
            .ok_or(MetadataError::StringNotTerminated { index })?;
        Ok(String::from_utf8_lossy(&slice[..nul]).into_owned())
    }

    pub fn image_name(&self, image: &ImageDefinition) -> Result<String, MetadataError> {
        self.string(image.name_index)
    }

    pub fn type_name(&self, td: &TypeDefinition) -> Result<String, MetadataError> {
        let name = self.string(td.name_index)?;
        let ns = self.string(td.namespace_index)?;
        if ns.is_empty() {
            Ok(name)
        } else {
            Ok(format!("{ns}.{name}"))
        }
    }

    pub fn nested_children(&self, td: &TypeDefinition) -> Vec<usize> {
        if td.nested_type_index < 0 || td.nested_type_count == 0 {
            return Vec::new();
        }
        let start = td.nested_type_index as usize;
        let end = start + td.nested_type_count as usize;
        self.nested_types
            .get(start..end)
            .into_iter()
            .flatten()
            .copied()
            .filter_map(|i| if i >= 0 { Some(i as usize) } else { None })
            .filter(|&i| i < self.type_defs.len())
            .collect()
    }

    pub fn interfaces_of(&self, td: &TypeDefinition) -> Vec<i32> {
        if td.interfaces_index < 0 || td.interfaces_count == 0 {
            return Vec::new();
        }
        let start = td.interfaces_index as usize;
        let end = start + td.interfaces_count as usize;
        self.interfaces.get(start..end).unwrap_or(&[]).to_vec()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rejects_short_and_bad_magic() {
        match Metadata::parse(vec![0; 16]) {
            Err(MetadataError::TooSmall { .. }) => {}
            Err(e) => panic!("unexpected error {e}"),
            Ok(_) => panic!("expected TooSmall"),
        }

        let mut buf = vec![0u8; ENDFIELD_HEADER_SIZE];
        buf[0..4].copy_from_slice(&0xDEAD_BEEFu32.to_le_bytes());
        buf[4..8].copy_from_slice(&29i32.to_le_bytes());
        match Metadata::parse(buf) {
            Err(MetadataError::BadMagic { .. }) => {}
            Err(e) => panic!("unexpected error {e}"),
            Ok(_) => panic!("expected BadMagic"),
        }
    }
}
