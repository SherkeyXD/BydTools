//! The checked-in fixture is a slice of a real Endfield export.
//! Wrapper objects keep the gold field names and wire_order meaning.

use beyond_mp_schema::schema::{SchemaDocument, ORDER_NOTE, PURPOSE, WIRE_ORDER_SOURCE};
use beyond_mp_schema::type_index::build_type_index;
use serde_json::Value;
use std::fs;
use std::path::PathBuf;

fn fixture() -> Value {
    let path = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("tests/fixtures/schemas-sample.json");
    let text = fs::read_to_string(&path).unwrap();
    serde_json::from_str(&text).unwrap()
}

#[test]
fn fixture_matches_gold_wire_order_rules() {
    let doc = fixture();
    assert_eq!(doc["wire_order_rule"], WIRE_ORDER_SOURCE);
    assert_eq!(doc["order_caveat"], ORDER_NOTE);
    assert_eq!(doc["reference_full_export"]["wrappers"], 5025);
    assert_eq!(doc["reference_full_export"]["serialize_markers"], 41);
    assert_eq!(
        doc["reference_full_export"]["wrappers_orders_differ_from_target_fields"],
        934
    );
    assert_eq!(
        doc["reference_full_export"]["wrappers_with_formatter"],
        5025
    );

    let parsed: SchemaDocument = serde_json::from_value(serde_json::json!({
        "generated_by": "fixture",
        "purpose": PURPOSE,
        "wire_order_rule": doc["wire_order_rule"],
        "order_caveat": doc["order_caveat"],
        "custom_attributes": doc["custom_attributes"],
        "related_exports": {"type_index": "type_index.json", "readme": "README.md", "note": "fixture"},
        "counts": doc["counts"],
        "wrappers": doc["wrappers"],
        "serialize_markers": doc["serialize_markers"],
    }))
    .expect("fixture wrappers deserialize into the exporter model");

    let required = [
        "wrapper_type",
        "typedef_index",
        "assembly",
        "target_type",
        "target_typedef_index",
        "target_assembly",
        "interfaces",
        "wire_order",
        "members_from_wrapper_props",
        "members_from_target_fields",
        "orders_differ",
        "has_formatter",
        "formatter_typedef",
        "formatter_type",
        "has_deserialize",
        "has_register_formatter",
        "wire_order_source",
        "order_note",
    ];
    for w in doc["wrappers"].as_array().unwrap() {
        for key in required {
            assert!(
                w.get(key).is_some(),
                "missing {key} on {}",
                w["wrapper_type"]
            );
        }
        assert_eq!(w["wire_order"], w["members_from_wrapper_props"]);
        let props: Vec<&str> = w["wire_order"]
            .as_array()
            .unwrap()
            .iter()
            .map(|m| m["name"].as_str().unwrap())
            .collect();
        let fields: Vec<&str> = w["members_from_target_fields"]
            .as_array()
            .unwrap()
            .iter()
            .map(|m| m["name"].as_str().unwrap())
            .collect();
        let differ = if props.is_empty() || fields.is_empty() {
            false
        } else if props.len() != fields.len() {
            true
        } else {
            let mut a = props.clone();
            let mut b = fields.clone();
            a.sort_unstable();
            b.sort_unstable();
            a != b || props != fields
        };
        assert_eq!(w["orders_differ"], differ, "{}", w["wrapper_type"]);
    }

    let by_target = |name: &str| {
        parsed
            .wrappers
            .iter()
            .find(|w| w.target_type.as_deref() == Some(name))
            .unwrap_or_else(|| panic!("missing {name}"))
    };

    let table = by_target("Beyond.Gameplay.BambooRaftTaskTable");
    assert_eq!(names(&table.wire_order), vec!["data"]);
    assert!(!table.orders_differ);

    let task = by_target("Beyond.Gameplay.BambooRaftTaskData");
    assert_eq!(names(&task.wire_order), vec!["questConfigs", "raftId"]);
    assert_eq!(
        task.members_from_target_fields
            .iter()
            .map(|f| f.name.as_str())
            .collect::<Vec<_>>(),
        vec!["raftId", "questConfigs"]
    );
    assert!(task.orders_differ);
    assert_eq!(
        task.wire_order[0].ty,
        "System.Collections.Generic.Dictionary`2<string, Beyond.Gameplay.BambooRaftQuestConfig>"
    );

    let quest = by_target("Beyond.Gameplay.BambooRaftQuestConfig");
    assert_eq!(names(&quest.wire_order), vec!["questId", "state"]);
    assert_eq!(quest.wire_order[0].ty, "string");
    assert_eq!(quest.wire_order[1].ty, "QuestState");

    let aabb = by_target("Beyond.AABB");
    assert_eq!(names(&aabb.wire_order), vec!["max", "min"]);
    assert_eq!(
        aabb.members_from_target_fields
            .iter()
            .map(|f| f.name.as_str())
            .collect::<Vec<_>>(),
        vec!["min", "max"]
    );
    assert!(aabb.orders_differ);

    let teleport = by_target("Beyond.Gameplay.TeleportValidationData");
    assert_eq!(
        names(&teleport.wire_order),
        vec![
            "deviation",
            "id",
            "keepAnim",
            "keepCam",
            "position",
            "rotationEuler",
            "sceneId",
            "subDataParentId",
            "teleportReason",
            "uiTypeOut",
        ]
    );
    assert_eq!(
        teleport.members_from_target_fields[0].name,
        "subDataParentId"
    );
    assert_eq!(teleport.wire_order[4].ty, "UnityEngine.Vector3");
    assert!(teleport.orders_differ);

    let index = build_type_index(
        &parsed.wrappers,
        &parsed.serialize_markers,
        &beyond_mp_schema::layout::TypeLayouts::default(),
    );
    assert_eq!(
        index["by_basename"]["BambooRaftTaskTable"][0],
        "Beyond.Gameplay.BambooRaftTaskTable"
    );
    assert_eq!(
        index["by_type"]["Beyond.Gameplay.BambooRaftTaskTable"]["serialize_marker"],
        true
    );
    assert_eq!(
        index["by_type"]["Beyond.Gameplay.BambooRaftTaskData"]["serialize_marker"],
        false
    );
    assert_eq!(
        index["by_type"]["Beyond.Gameplay.BambooRaftTaskData"]["entries"][0]["wire"][0]["kind"],
        "dictionary"
    );
    assert_eq!(
        index["by_type"]["Beyond.Gameplay.BambooRaftQuestConfig"]["entries"][0]["wire"][0]["kind"],
        "string"
    );
    assert_eq!(
        index["by_type"]["Beyond.AABB"]["entries"][0]["wire"][0]["kind"],
        "vector2"
    );
}

fn names(props: &[beyond_mp_schema::schema::MemberProp]) -> Vec<&str> {
    props.iter().map(|p| p.name.as_str()).collect()
}
