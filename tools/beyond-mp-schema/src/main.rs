//! CLI for Endfield Beyond.MemoryPack schema export.

use anyhow::{bail, Context, Result};
use beyond_mp_schema::decode::DecodeOptions;
use beyond_mp_schema::reg::LocateOptions;
use beyond_mp_schema::stringpool::StringTable;
use beyond_mp_schema::{
    decode_schema_json, export_bundle, locate_metadata_registration, write_bundle, Metadata,
    PeImage,
};
use clap::{Parser, Subcommand};
use std::fs;
use std::io::{self, Write};
use std::path::{Path, PathBuf};

#[derive(Parser, Debug)]
#[command(
    name = "beyond-mp-schema",
    version,
    about = "Export Endfield Beyond.MemoryPack schemas for BydTools (primary command: export)"
)]
struct Cli {
    #[command(subcommand)]
    cmd: Cmd,
}

#[derive(Subcommand, Debug)]
enum Cmd {
    /// Write schemas.json, type_index.json, type.cs, and README.md.
    Export {
        /// `global-metadata.dat` (Endfield_Data/il2cpp_data/Metadata/).
        #[arg(long)]
        metadata: Option<PathBuf>,
        /// Windows `GameAssembly.dll` next to the game executable.
        #[arg(long)]
        gameassembly: Option<PathBuf>,
        /// Game root. Fills in --metadata and --gameassembly when omitted.
        #[arg(long)]
        game_root: Option<PathBuf>,
        /// Output directory (schemas.json, type_index.json, type.cs, README.md).
        #[arg(long, default_value = "out/memorypack")]
        out: PathBuf,
        /// Ignore the known Endfield MetadataRegistration RVA and scan .rdata.
        #[arg(long, default_value_t = false)]
        scan: bool,
    },
    /// Experimental sample. Not the export contract; BydTools deserializes JsonData.
    Decode {
        /// `schemas.json` from `export`.
        #[arg(long)]
        schemas: PathBuf,
        /// Decrypted JsonData file (still often named `*.json`).
        #[arg(long)]
        input: PathBuf,
        /// Target type, wrapper type, or basename (`BambooRaftTaskTable`).
        #[arg(long = "type")]
        type_name: String,
        /// Disambiguate when several wrappers share a target type.
        #[arg(long)]
        wrapper: Option<String>,
        /// Directory of `StringPoolN.byte` (USPC) files.
        #[arg(long)]
        string_pool: Option<PathBuf>,
        /// JSON object of pool id → string, merged after --string-pool.
        #[arg(long)]
        string_map: Option<PathBuf>,
        /// Write JSON here. Omit to print to stdout.
        #[arg(long)]
        output: Option<PathBuf>,
    },
}

fn main() -> Result<()> {
    let cli = Cli::parse();
    match cli.cmd {
        Cmd::Export {
            metadata,
            gameassembly,
            game_root,
            out,
            scan,
        } => cmd_export(metadata, gameassembly, game_root, out, scan),
        Cmd::Decode {
            schemas,
            input,
            type_name,
            wrapper,
            string_pool,
            string_map,
            output,
        } => cmd_decode(
            schemas,
            input,
            type_name,
            wrapper,
            string_pool,
            string_map,
            output,
        ),
    }
}

fn cmd_export(
    metadata: Option<PathBuf>,
    gameassembly: Option<PathBuf>,
    game_root: Option<PathBuf>,
    out: PathBuf,
    scan: bool,
) -> Result<()> {
    let (metadata, gameassembly) = resolve_inputs(metadata, gameassembly, game_root)?;
    eprintln!("metadata    {}", metadata.display());
    eprintln!("gameassembly {}", gameassembly.display());
    let meta = Metadata::from_path(&metadata)
        .with_context(|| format!("load metadata {}", metadata.display()))?;
    let pe = PeImage::from_path(&gameassembly)
        .with_context(|| format!("load GameAssembly {}", gameassembly.display()))?;
    let located = locate_metadata_registration(
        &pe,
        meta.type_defs.len() as u64,
        &LocateOptions {
            force_scan: scan,
            known_rva: None,
        },
    )?;
    if let Some(note) = &located.note {
        eprintln!("{note}");
    }
    eprintln!(
        "registration {} at {:#x} (types={}, typedefs={})",
        located.source,
        located.reg.va,
        located.reg.types_count,
        meta.type_defs.len()
    );
    let bundle = export_bundle(&meta, &pe, &located.reg, located.source)?;
    let paths = write_bundle(&bundle, &out)?;
    let c = &bundle.schemas.counts;
    eprintln!(
        "wrappers={} markers={} orders_differ={} with_formatter={}",
        c.wrappers,
        c.serialize_markers,
        c.wrappers_orders_differ_from_target_fields,
        c.wrappers_with_formatter
    );
    for p in &paths {
        let n = fs::metadata(p).map(|m| m.len()).unwrap_or(0);
        eprintln!("wrote {} ({n} bytes)", p.display());
    }
    Ok(())
}

fn cmd_decode(
    schemas: PathBuf,
    input: PathBuf,
    type_name: String,
    wrapper: Option<String>,
    string_pool: Option<PathBuf>,
    string_map: Option<PathBuf>,
    output: Option<PathBuf>,
) -> Result<()> {
    let text =
        fs::read_to_string(&schemas).with_context(|| format!("read {}", schemas.display()))?;
    let doc: serde_json::Value =
        serde_json::from_str(&text).with_context(|| format!("parse {}", schemas.display()))?;
    let bytes = fs::read(&input).with_context(|| format!("read {}", input.display()))?;
    let mut strings = StringTable::new();
    if let Some(dir) = &string_pool {
        let n = strings.load_pool_dir(dir)?;
        eprintln!("string pool {n} strings from {}", dir.display());
    }
    if let Some(map) = &string_map {
        let n = strings.load_json_map(map)?;
        eprintln!("string map {n} entries from {}", map.display());
    }
    let value = decode_schema_json(
        &doc,
        &bytes,
        &DecodeOptions {
            type_name: &type_name,
            wrapper: wrapper.as_deref(),
            strings: &strings,
        },
    )?;
    let pretty = serde_json::to_string_pretty(&value)? + "\n";
    if let Some(path) = output {
        if let Some(parent) = path.parent() {
            if !parent.as_os_str().is_empty() {
                fs::create_dir_all(parent)?;
            }
        }
        fs::write(&path, &pretty).with_context(|| format!("write {}", path.display()))?;
        eprintln!("wrote {}", path.display());
    } else {
        let mut stdout = io::stdout().lock();
        stdout.write_all(pretty.as_bytes())?;
    }
    Ok(())
}

fn resolve_inputs(
    metadata: Option<PathBuf>,
    gameassembly: Option<PathBuf>,
    game_root: Option<PathBuf>,
) -> Result<(PathBuf, PathBuf)> {
    let mut metadata = metadata;
    let mut gameassembly = gameassembly;
    if let Some(root) = &game_root {
        if metadata.is_none() {
            metadata = Some(find_metadata(root)?);
        }
        if gameassembly.is_none() {
            gameassembly = Some(find_gameassembly(root)?);
        }
    }
    match (metadata, gameassembly) {
        (Some(m), Some(g)) => Ok((m, g)),
        _ => bail!(
            "need --metadata and --gameassembly, or --game-root pointing at an Endfield PC install"
        ),
    }
}

fn find_gameassembly(root: &Path) -> Result<PathBuf> {
    let dll = root.join("GameAssembly.dll");
    if dll.is_file() {
        return Ok(dll);
    }
    let so = root.join("GameAssembly.so");
    if so.is_file() {
        bail!(
            "found {} but this tool reads the Windows PE64 GameAssembly.dll",
            so.display()
        );
    }
    bail!("GameAssembly.dll not found in {}", root.display());
}

fn find_metadata(root: &Path) -> Result<PathBuf> {
    let direct = root.join("Endfield_Data/il2cpp_data/Metadata/global-metadata.dat");
    if direct.is_file() {
        return Ok(direct);
    }
    let data_root = root
        .read_dir()
        .with_context(|| format!("read {}", root.display()))?;
    let mut hits = Vec::new();
    for ent in data_root.flatten() {
        let name = ent.file_name();
        let name = name.to_string_lossy();
        if !name.ends_with("_Data") {
            continue;
        }
        let candidate = ent.path().join("il2cpp_data/Metadata/global-metadata.dat");
        if candidate.is_file() {
            hits.push(candidate);
        }
    }
    hits.sort();
    if hits.len() == 1 {
        return Ok(hits.remove(0));
    }
    if hits.len() > 1 {
        bail!(
            "several global-metadata.dat files under {}; pass --metadata",
            root.display()
        );
    }
    bail!(
        "global-metadata.dat not found under {} (expected *_Data/il2cpp_data/Metadata/)",
        root.display()
    );
}
