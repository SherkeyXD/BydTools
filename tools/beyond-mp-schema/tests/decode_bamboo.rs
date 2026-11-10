//! Decode the shipped BambooRaftTaskTable sample with the gold wire_order.

use beyond_mp_schema::decode::{decode_schema_json, DecodeOptions};
use beyond_mp_schema::stringpool::StringTable;
use serde_json::Value;
use std::fs;
use std::path::PathBuf;

fn root() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
}

#[test]
fn bamboo_matches_resolved_sample() {
    let schemas: Value = serde_json::from_str(
        &fs::read_to_string(root().join("tests/fixtures/schemas-sample.json")).unwrap(),
    )
    .unwrap();
    let bytes = fs::read(root().join("tests/fixtures/bamboo/BambooRaftTaskTable.json")).unwrap();
    let expected: Value = serde_json::from_str(
        &fs::read_to_string(root().join("tests/fixtures/bamboo/expected.json")).unwrap(),
    )
    .unwrap();
    let mut strings = StringTable::new();
    strings
        .load_json_map(&root().join("tests/fixtures/bamboo/string-map.json"))
        .unwrap();

    let decoded = decode_schema_json(
        &schemas,
        &bytes,
        &DecodeOptions {
            type_name: "BambooRaftTaskTable",
            wrapper: None,
            strings: &strings,
        },
    )
    .expect("decode");
    assert_eq!(decoded, expected);

    let unresolved = decode_schema_json(
        &schemas,
        &bytes,
        &DecodeOptions {
            type_name: "Beyond.Gameplay.BambooRaftTaskTable",
            wrapper: None,
            strings: &StringTable::new(),
        },
    )
    .unwrap();
    let first = &unresolved["data"]["10100020327"]["questConfigs"];
    let obj = first.as_object().unwrap();
    assert!(
        obj.contains_key("#66099"),
        "keys: {:?}",
        obj.keys().collect::<Vec<_>>()
    );
    assert_eq!(obj["#66099"]["questId"]["$stringPool"], 66099);
    assert_eq!(obj["#66099"]["state"], 2);
    assert_eq!(unresolved["data"]["10100020327"]["raftId"], 0);
}

#[test]
fn rejects_plain_json() {
    let schemas: Value = serde_json::from_str(
        &fs::read_to_string(root().join("tests/fixtures/schemas-sample.json")).unwrap(),
    )
    .unwrap();
    let err = decode_schema_json(
        &schemas,
        br#"{"data":[]}"#,
        &DecodeOptions {
            type_name: "Beyond.Gameplay.BambooRaftTaskTable",
            wrapper: None,
            strings: &StringTable::new(),
        },
    )
    .unwrap_err();
    let msg = format!("{err:#}");
    assert!(msg.contains("UTF-8 JSON"), "{msg}");
}
