use super::{AuthError, Event, fail};
use axum::http::StatusCode;
use chrono::Datelike;
pub(super) fn upcoming(e: &Event, today: chrono::NaiveDate) -> bool {
	e.date_year.is_none_or(|y| {
		y > today.year()
			|| (y == today.year()
				&& e.date_month.is_none_or(|m| {
					m > today.month() as i32
						|| (m == today.month() as i32
							&& e.date_day.is_none_or(|d| d >= today.day() as i32))
				}))
	})
}
pub(super) fn precision(year: Option<i32>, month: Option<i32>, day: Option<i32>) -> &'static str {
	if day.is_some() && month.is_some() {
		"day"
	} else if month.is_some() {
		"month"
	} else if year.is_some() {
		"year"
	} else {
		"unknown"
	}
}
pub(super) fn compare(a: &Event, b: &Event) -> std::cmp::Ordering {
	if a.date_year.is_none() || b.date_year.is_none() {
		return b.date_year.cmp(&a.date_year).then(a.id.cmp(&b.id));
	}
	b.date_year
		.cmp(&a.date_year)
		.then_with(|| {
			let rank = |e: &Event| {
				if e.date_month.is_none() {
					2
				} else if e.date_day.is_none() {
					1
				} else {
					0
				}
			};
			rank(a).cmp(&rank(b))
		})
		.then(b.date_month.cmp(&a.date_month))
		.then(b.date_day.cmp(&a.date_day))
		.then(a.id.cmp(&b.id))
}
pub(super) fn validate(y: Option<i32>, m: Option<i32>, d: Option<i32>) -> Result<(), AuthError> {
	let message = if (m.is_some() && y.is_none()) || (d.is_some() && m.is_none()) {
		Some("Das Datum ist unvollständig.")
	} else if y.is_some_and(|y| !(1800..=2100).contains(&y)) {
		Some("Das Jahr liegt außerhalb des möglichen Bereichs.")
	} else if m.is_some_and(|m| !(1..=12).contains(&m))
		|| d.is_some_and(|d| {
			chrono::NaiveDate::from_ymd_opt(
				y.unwrap_or_default(),
				m.unwrap_or_default() as u32,
				d as u32,
			)
			.is_none()
		}) {
		Some("Das Datum passt nicht zum angegebenen Monat.")
	} else {
		None
	};
	message.map_or(Ok(()), |m| Err(fail(StatusCode::BAD_REQUEST, m)))
}
