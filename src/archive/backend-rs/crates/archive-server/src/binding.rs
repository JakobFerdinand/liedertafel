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
	EventCreate,
	EventPatch,
	EventDate,
	Programme,
	ProgrammeItem,
	EventDraft,
	DateiPatch,
	QueueEnvelope,
	RecordingCreate,
	RecordingPatch,
	RecordingAttach,
	PassageCreate,
	PassagePatch,
	PassageDelete,
	PassageReview,
	PassageReviewItem,
}
#[derive(Clone, Copy)]
enum Property {
	String,
	U32,
	WebU32,
	Double,
	I32,
	I64,
	Bool,
	RequiredBool,
	Guid,
	RequiredGuid,
	Record(Shape),
	Files,
	Items,
	Reviews,
	Opaque,
	Strings,
	Unknown,
}
impl Shape {
	fn property(self, name: &str) -> Property {
		use Property::*;
		match self {
			Self::QueueEnvelope => match name {
				"type" | "traceParent" | "traceState" => String,
				"dateiId" => RequiredGuid,
				_ => Unknown,
			},
			Self::PassageCreate | Self::PassagePatch => match name {
				"programmeitemid" if matches!(self, Self::PassageCreate) => Guid,
				"expectedplaybackdateiid" => Guid,
				"startseconds" | "endseconds" => Double,
				"expectedversion" if matches!(self, Self::PassagePatch) => WebU32,
				_ => Unknown,
			},
			Self::PassageDelete | Self::PassageReviewItem => match name {
				"expectedversion" => WebU32,
				"id" if matches!(self, Self::PassageReviewItem) => Guid,
				_ => Unknown,
			},
			Self::PassageReview => match name {
				"expectedplaybackdateiid" => Guid,
				"passages" => Reviews,
				_ => Unknown,
			},
			Self::RecordingCreate => match name {
				"label" | "kind" => String,
				_ => Unknown,
			},
			Self::RecordingPatch => match name {
				"label" => String,
				"ispublished" | "downloadenabled" => Bool,
				"durationseconds" => Double,
				"expectedversion" => WebU32,
				_ => Unknown,
			},
			Self::RecordingAttach => match name {
				"dateiid" => Guid,
				_ => Unknown,
			},
			Self::DateiPatch => match name {
				"description" => String,
				"rowversion" => WebU32,
				_ => Unknown,
			},
			Self::EventDraft => match name {
				"entwurfid" => Guid,
				"titel" => String,
				"dateien" => I32,
				"formular" => Opaque,
				_ => Unknown,
			},
			Self::Programme => match name {
				"items" => Items,
				"rowversion" => WebU32,
				_ => Unknown,
			},
			Self::ProgrammeItem => match name {
				"id" | "liedid" => Guid,
				"note" => String,
				_ => Unknown,
			},
			Self::EventCreate | Self::EventPatch => match name {
				"kind" | "title" | "venue" | "starttime" | "notes" | "sourcenote" => String,
				"dateyear" | "datemonth" | "dateday" if matches!(self, Self::EventCreate) => I32,
				"dateapproximate" if matches!(self, Self::EventCreate) => RequiredBool,
				"date" if matches!(self, Self::EventPatch) => Record(Self::EventDate),
				"expectedrowversion" if matches!(self, Self::EventPatch) => WebU32,
				_ => Unknown,
			},
			Self::EventDate => match name {
				"year" | "month" | "day" => I32,
				"approximate" => RequiredBool,
				_ => Unknown,
			},
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
			let property = self.0.property(if matches!(self.0, Shape::QueueEnvelope) {
				&key
			} else {
				&normalized
			});
			if matches!(
				self.0,
				Shape::NullableStrings(_)
					| Shape::EventCreate
					| Shape::EventPatch
					| Shape::EventDate
					| Shape::Programme
					| Shape::ProgrammeItem
					| Shape::EventDraft
					| Shape::DateiPatch
					| Shape::QueueEnvelope
					| Shape::RecordingCreate
					| Shape::RecordingPatch
					| Shape::RecordingAttach
					| Shape::PassageCreate
					| Shape::PassagePatch
					| Shape::PassageDelete
					| Shape::PassageReview
					| Shape::PassageReviewItem
			) && matches!(property, Property::Unknown)
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
				Property::Double => {
					double::<A::Error>(map.next_value::<Box<serde_json::value::RawValue>>()?.get())?
				}
				Property::I32 => integer::<i32, A::Error>(map.next_value()?)?,
				Property::I64 => integer::<i64, A::Error>(map.next_value()?)?,
				Property::Bool => typed!(bool),
				Property::RequiredBool => Value::Bool(map.next_value::<bool>()?),
				Property::Guid => guid::<A::Error>(map.next_value()?)?,
				Property::RequiredGuid => guid::<A::Error>(Some(map.next_value::<String>()?))?,
				Property::Strings => typed!(Vec<Option<String>>),
				Property::Record(shape) => map.next_value_seed(Record(shape))?,
				Property::Files => map.next_value_seed(Files)?,
				Property::Items => map.next_value_seed(Items)?,
				Property::Reviews => map.next_value_seed(Reviews)?,
				// JsonElement is opaque data. Retain duplicates, whitespace and
				// number lexemes rather than eagerly rebuilding a JSON map.
				Property::Opaque => Value::String(
					map.next_value::<Box<serde_json::value::RawValue>>()?
						.get()
						.to_owned(),
				),
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
// Ordinary web Double accepts quoted signs/exponents/named nonfinite values,
// but not surrounding whitespace. Retain nonfinite values as named strings so
// endpoint domain validation (not the JSON binder) supplies its specific error.
fn double<E: serde::de::Error>(raw: &str) -> Result<Value, E> {
	if raw == "null" {
		return Ok(Value::Null);
	}
	let text = if raw.starts_with('"') {
		serde_json::from_str::<String>(raw).map_err(E::custom)?
	} else {
		raw.to_owned()
	};
	if text.is_empty() || text.trim() != text {
		return Err(E::custom("invalid double"));
	}
	let number = text.parse::<f64>().map_err(E::custom)?;
	if !number.is_finite() {
		// CLR names are case-sensitive; Rust also accepts inf/nan spellings.
		if raw.starts_with('"') && !matches!(text.as_str(), "NaN" | "Infinity" | "-Infinity") {
			return Err(E::custom("invalid named double"));
		}
		return Ok(Value::String(
			if number.is_nan() {
				"NaN"
			} else if number.is_sign_negative() {
				"-Infinity"
			} else {
				"Infinity"
			}
			.into(),
		));
	}
	serde_json::to_value(number).map_err(E::custom)
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
struct Items;
struct Reviews;
impl<'de> DeserializeSeed<'de> for Reviews {
	type Value = Value;
	fn deserialize<D: serde::Deserializer<'de>>(self, d: D) -> Result<Value, D::Error> {
		d.deserialize_any(self)
	}
}
impl<'de> Visitor<'de> for Reviews {
	type Value = Value;
	fn expecting(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
		f.write_str("review items or null")
	}
	fn visit_unit<E: serde::de::Error>(self) -> Result<Value, E> {
		Ok(Value::Null)
	}
	fn visit_seq<A: SeqAccess<'de>>(self, mut array: A) -> Result<Value, A::Error> {
		let mut values = Vec::new();
		while let Some(v) = array.next_element_seed(Record(Shape::PassageReviewItem))? {
			values.push(v);
		}
		Ok(Value::Array(values))
	}
}
impl<'de> DeserializeSeed<'de> for Items {
	type Value = Value;
	fn deserialize<D: serde::Deserializer<'de>>(self, d: D) -> Result<Value, D::Error> {
		d.deserialize_any(self)
	}
}
impl<'de> Visitor<'de> for Items {
	type Value = Value;
	fn expecting(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
		f.write_str("programme items or null")
	}
	fn visit_unit<E: serde::de::Error>(self) -> Result<Value, E> {
		Ok(Value::Null)
	}
	fn visit_seq<A: SeqAccess<'de>>(self, mut array: A) -> Result<Value, A::Error> {
		let mut items = Vec::new();
		while let Some(item) = array.next_element_seed(Record(Shape::ProgrammeItem))? {
			items.push(item);
		}
		Ok(Value::Array(items))
	}
}
