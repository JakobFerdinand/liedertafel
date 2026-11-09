//! Only the catalogue's frozen request records. Validate each wire occurrence
//! before retaining its last value; a Value/preserve_order map would already
//! have discarded exact duplicates and their earlier type errors.
use serde::de::{DeserializeSeed, MapAccess, SeqAccess, Visitor};
use serde_json::Value;

#[derive(Clone, Copy)]
pub(super) enum Shape {
	Create,
	Patch,
	Upload,
	Finalize,
	Draft,
	DraftLied,
	DraftFile,
	DraftReading,
	Suggestions,
	Version,
}
#[derive(Clone, Copy)]
enum Property {
	String,
	U32,
	I32,
	I64,
	Bool,
	Guid,
	Record(Shape),
	Files,
	Strings,
	Unknown,
}
impl Shape {
	fn property(self, name: &str) -> Property {
		use Property::*;
		match self {
			Self::Create | Self::Patch | Self::DraftLied => {
				if name == "title"
					|| super::writes::FIELDS.iter().any(|(form, _)| {
						(!matches!(self, Self::DraftLied) || *form != "occasion")
							&& name.eq_ignore_ascii_case(form)
					}) {
					String
				} else if matches!(self, Self::Patch) && name == "rowversion" {
					U32
				} else {
					Unknown
				}
			}
			Self::Upload => match name {
				"kind" | "stimme" | "description" | "filename" | "contenttype" => String,
				"ispartitur" => Bool,
				"sizebytes" => I64,
				"replacesdateiid" | "recordedateventid" => Guid,
				_ => Unknown,
			},
			Self::Finalize => match name {
				"filename" => String,
				"sizebytes" => I64,
				_ => Unknown,
			},
			Self::Version => {
				if name == "rowversion" {
					U32
				} else {
					Unknown
				}
			}
			Self::Draft => match name {
				"entwurfid" => Guid,
				"lied" => Record(Self::DraftLied),
				"dateien" => Files,
				"bearbeitet" => Strings,
				"vorschlaege" => Record(Self::Suggestions),
				_ => Unknown,
			},
			Self::DraftFile => match name {
				"id" => Guid,
				"name" | "art" | "stimme" => String,
				"sizebytes" => I64,
				"artbearbeitet" | "stimmebearbeitet" => Bool,
				"lesen" => Record(Self::DraftReading),
				_ => Unknown,
			},
			Self::DraftReading => match name {
				"status" | "meldung" => String,
				"anfrageid" => I32,
				_ => Unknown,
			},
			Self::Suggestions => String,
		}
	}
}

struct Record(Shape);
impl<'de> DeserializeSeed<'de> for Record {
	type Value = Value;
	fn deserialize<D: serde::Deserializer<'de>>(self, deserializer: D) -> Result<Value, D::Error> {
		deserializer.deserialize_any(self)
	}
}
impl<'de> Visitor<'de> for Record {
	type Value = Value;
	fn expecting(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
		f.write_str("a catalogue request object or null")
	}
	fn visit_unit<E: serde::de::Error>(self) -> Result<Value, E> {
		Ok(Value::Null)
	}
	fn visit_map<A: MapAccess<'de>>(self, mut map: A) -> Result<Value, A::Error> {
		let mut values = serde_json::Map::new();
		while let Some(key) = map.next_key::<String>()? {
			let normalized = key.to_ascii_lowercase();
			let property = self.0.property(&normalized);
			// These are the nullable CLR property types, not domain validation:
			// e.g. an earlier long string may be replaced by a shorter string,
			// but an earlier number can never become a valid String binding.
			macro_rules! typed {
				($t:ty) => {
					serde_json::to_value(map.next_value::<Option<$t>>()?)
						.map_err(serde::de::Error::custom)?
				};
			}
			let value = match property {
				Property::String => typed!(String),
				Property::U32 => typed!(u32),
				Property::I32 => integer::<i32, A::Error>(map.next_value()?)?,
				Property::I64 => integer::<i64, A::Error>(map.next_value()?)?,
				Property::Bool => typed!(bool),
				Property::Guid => typed!(uuid::Uuid),
				Property::Strings => typed!(Vec<Option<String>>),
				Property::Record(shape) => map.next_value_seed(Record(shape))?,
				Property::Files => map.next_value_seed(Files)?,
				Property::Unknown => map.next_value::<Value>()?,
			};
			// Dictionary keys and unknown Lied fields remain case-sensitive data.
			// Every recognized record property has exactly one canonical key.
			let key =
				if matches!(self.0, Shape::Suggestions) || matches!(property, Property::Unknown) {
					key
				} else {
					normalized
				};
			values.insert(key, value);
		}
		Ok(Value::Object(values))
	}
}
// HTTP's ordinary integer converters accept quoted integers; the custom Lied
// rowVersion converter (U32 above) deliberately does not. Parse each occurrence.
fn integer<T, E>(value: Value) -> Result<Value, E>
where
	T: serde::de::DeserializeOwned + serde::Serialize + std::str::FromStr,
	E: serde::de::Error,
{
	if value.is_null() {
		return Ok(value);
	}
	let number: T = if let Some(text) = value.as_str() {
		text.parse()
			.map_err(|_| E::custom("invalid quoted integer"))?
	} else {
		serde_json::from_value(value).map_err(E::custom)?
	};
	serde_json::to_value(number).map_err(E::custom)
}
struct Files;
impl<'de> DeserializeSeed<'de> for Files {
	type Value = Value;
	fn deserialize<D: serde::Deserializer<'de>>(self, deserializer: D) -> Result<Value, D::Error> {
		deserializer.deserialize_any(self)
	}
}
impl<'de> Visitor<'de> for Files {
	type Value = Value;
	fn expecting(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
		f.write_str("draft Dateien or null")
	}
	fn visit_unit<E: serde::de::Error>(self) -> Result<Value, E> {
		Ok(Value::Null)
	}
	fn visit_seq<A: SeqAccess<'de>>(self, mut array: A) -> Result<Value, A::Error> {
		let mut files = Vec::new();
		while let Some(file) = array.next_element_seed(Record(Shape::DraftFile))? {
			files.push(file);
		}
		Ok(Value::Array(files))
	}
}
pub(super) fn parse(bytes: &[u8], shape: Shape) -> Result<Value, serde_json::Error> {
	let mut deserializer = serde_json::Deserializer::from_slice(bytes);
	let value = Record(shape).deserialize(&mut deserializer)?;
	deserializer.end()?;
	Ok(value)
}
