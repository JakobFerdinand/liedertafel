//! Only the implemented frozen request records. Validate each wire occurrence
//! before retaining its last value; a Value/preserve_order map would already
//! have discarded exact duplicates and their earlier type errors.
use serde::de::{DeserializeSeed, MapAccess, SeqAccess, Visitor};
use serde_json::Value;

#[derive(Clone, Copy)]
pub(crate) enum Shape {
	NullableStrings(&'static [&'static str]),
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
	WebU32,
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
			Self::NullableStrings(fields) => {
				if fields.iter().any(|f| name.eq_ignore_ascii_case(f)) {
					String
				} else {
					Unknown
				}
			}
			Self::Create | Self::Patch | Self::DraftLied => {
				if name == "title"
					|| matches!(
						name,
						"composer"
							| "arranger" | "lyricist"
							| "voiceconfiguration"
							| "musicalkey" | "language"
							| "lyrics"
					) || (name == "occasion" && !matches!(self, Self::DraftLied))
				{
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
					WebU32
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
		f.write_str("a request object or null")
	}
	fn visit_unit<E: serde::de::Error>(self) -> Result<Value, E> {
		Ok(Value::Null)
	}
	fn visit_map<A: MapAccess<'de>>(self, mut map: A) -> Result<Value, A::Error> {
		let mut values = serde_json::Map::new();
		while let Some(key) = map.next_key::<String>()? {
			let normalized = key.to_ascii_lowercase();
			let property = self.0.property(&normalized);
			if matches!(self.0, Shape::NullableStrings(_)) && matches!(property, Property::Unknown)
			{
				map.next_value::<serde::de::IgnoredAny>()?;
				continue;
			}
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
				Property::WebU32 => web_u32::<A::Error>(map.next_value()?)?,
				Property::I32 => integer::<i32, A::Error>(map.next_value()?)?,
				Property::I64 => integer::<i64, A::Error>(map.next_value()?)?,
				Property::Bool => typed!(bool),
				Property::Guid => guid::<A::Error>(map.next_value()?)?,
				Property::Strings => typed!(Vec<Option<String>>),
				Property::Record(shape) => map.next_value_seed(Record(shape))?,
				Property::Files => map.next_value_seed(Files)?,
				Property::Unknown => map.next_value::<Value>()?,
			};
			// Dictionary keys and unknown Lied fields remain case-sensitive data.
			// Every recognized record property has exactly one canonical key.
			let key = if let Shape::NullableStrings(fields) = self.0 {
				fields
					.iter()
					.find(|f| f.eq_ignore_ascii_case(&key))
					.map_or(key.clone(), |f| (*f).to_owned())
			} else if matches!(self.0, Shape::Suggestions) || matches!(property, Property::Unknown)
			{
				key
			} else {
				normalized
			};
			values.insert(key, value);
		}
		Ok(Value::Object(values))
	}
}
// Only typed Guid? DTO properties use System.Text.Json's 36-character D format.
// Keep string AccountId/domain/CLI/path parsing outside this converter.
fn guid<E: serde::de::Error>(value: Option<String>) -> Result<Value, E> {
	let Some(text) = value else {
		return Ok(Value::Null);
	};
	if text.len() != 36 {
		return Err(E::custom("expected a D-format Guid"));
	}
	let id = uuid::Uuid::parse_str(&text).map_err(E::custom)?;
	serde_json::to_value(id).map_err(E::custom)
}
// The executed ordinary uint oracle accepts quoted ASCII digits/leading zeros,
// but unlike signed integers it rejects both '+' and '-' (even '-0').
fn web_u32<E: serde::de::Error>(value: Value) -> Result<Value, E> {
	if let Some(text) = value.as_str()
		&& (text.is_empty() || !text.bytes().all(|b| b.is_ascii_digit()))
	{
		return Err(E::custom("invalid quoted unsigned integer"));
	}
	integer::<u32, E>(value)
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
pub(crate) fn parse(bytes: &[u8], shape: Shape) -> Result<Value, serde_json::Error> {
	let mut deserializer = serde_json::Deserializer::from_slice(bytes);
	let value = Record(shape).deserialize(&mut deserializer)?;
	deserializer.end()?;
	Ok(value)
}
