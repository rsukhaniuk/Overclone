//! Strict NEW-profile shape only; legacy envelope acceptance is unchanged.
use serde::de::{self, MapAccess, SeqAccess, Visitor};
use serde::{Deserialize, Deserializer};
use serde_json::{Map, Value};
use std::fmt;

struct Unique(Value);
impl<'de> Deserialize<'de> for Unique {
    fn deserialize<D: Deserializer<'de>>(d: D) -> Result<Self, D::Error> {
        struct V;
        impl<'de> Visitor<'de> for V {
            type Value = Unique;
            fn expecting(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
                f.write_str("JSON without duplicate keys")
            }
            fn visit_map<M: MapAccess<'de>>(self, mut m: M) -> Result<Unique, M::Error> {
                let mut out = Map::new();
                while let Some(key) = m.next_key::<String>()? {
                    if out.contains_key(&key) {
                        return Err(de::Error::custom("duplicate field"));
                    }
                    out.insert(key, m.next_value::<Unique>()?.0);
                }
                Ok(Unique(Value::Object(out)))
            }
            fn visit_seq<S: SeqAccess<'de>>(self, mut s: S) -> Result<Unique, S::Error> {
                let mut out = Vec::new();
                while let Some(value) = s.next_element::<Unique>()? {
                    out.push(value.0);
                }
                Ok(Unique(Value::Array(out)))
            }
            fn visit_str<E: de::Error>(self, v: &str) -> Result<Unique, E> {
                Ok(Unique(Value::String(v.into())))
            }
            fn visit_bool<E: de::Error>(self, v: bool) -> Result<Unique, E> {
                Ok(Unique(Value::Bool(v)))
            }
            fn visit_u64<E: de::Error>(self, v: u64) -> Result<Unique, E> {
                Ok(Unique(Value::Number(v.into())))
            }
            fn visit_i64<E: de::Error>(self, v: i64) -> Result<Unique, E> {
                Ok(Unique(Value::Number(v.into())))
            }
            fn visit_f64<E: de::Error>(self, v: f64) -> Result<Unique, E> {
                serde_json::Number::from_f64(v)
                    .map(|n| Unique(Value::Number(n)))
                    .ok_or_else(|| de::Error::custom("invalid number"))
            }
            fn visit_unit<E: de::Error>(self) -> Result<Unique, E> {
                Ok(Unique(Value::Null))
            }
        }
        d.deserialize_any(V)
    }
}
fn keys(value: &Value, allowed: &[&str]) -> bool {
    value
        .as_object()
        .is_some_and(|m| m.keys().all(|k| allowed.contains(&k.as_str())))
}

pub(super) fn validate(json: &str) -> bool {
    let Ok(Unique(root)) = serde_json::from_str(json) else {
        return false;
    };
    if !keys(
        &root,
        &["protocolVersion", "requestId", "operation", "payload"],
    ) {
        return false;
    }
    let p = &root["payload"];
    if !keys(
        p,
        &[
            "snapshotId",
            "projectId",
            "sessionId",
            "workingGeneration",
            "baseHeadRevisionId",
            "profile",
            "referenceConfiguration",
            "limits",
            "files",
        ],
    ) || !keys(&p["profile"], &["id", "version"])
        || !keys(&p["referenceConfiguration"], &["rootFileId"])
    {
        return false;
    }
    if let Some(limits) = p.get("limits") {
        if !keys(
            limits,
            &[
                "maxBytesPerFile",
                "maxFiles",
                "maxTotalBytes",
                "maxResultBytes",
                "maxTokensPerFile",
                "maxTokensPerProject",
                "maxDiagnosticsPerProject",
                "maxBraceDepth",
            ],
        ) {
            return false;
        }
    }
    p["files"].as_array().is_some_and(|files| {
        files.iter().all(|f| {
            keys(
                f,
                &[
                    "fileId",
                    "relativePath",
                    "mediaKind",
                    "hasBom",
                    "sha256",
                    "byteOffset",
                    "byteLength",
                ],
            )
        })
    })
}
