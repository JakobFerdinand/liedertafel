//! Event-owned sonstiges metadata only. Upload/replace/trash/access use #224.
use super::*;
use crate::catalogue::queries;
pub(super) async fn list(pool: &sqlx::PgPool, event: Uuid) -> Result<Vec<Value>, AuthError> {
	let mut files = queries::dateien(pool, None, None).await?;
	files.retain(|d| d.event_id == Some(event) && d.kind == "sonstiges");
	files.sort_by(|a, b| a.created_at.cmp(&b.created_at).then(a.id.cmp(&b.id)));
	Ok(files.iter().map(|d|json!({"id":d.id,"art":if d.content_type.get(..6).is_some_and(|p|p.eq_ignore_ascii_case("image/")){"photo"}else{"document"},"description":d.description,"fileName":d.original_file_name,"contentType":d.content_type,"sizeBytes":d.size_bytes,"createdAt":d.created_at,"rowVersion":d.row_version})).collect())
}
pub(super) async fn patch(
	State(origin): State<Arc<Origin>>,
	Path(id): Path<Uuid>,
	request: Request,
) -> Result<Response, AuthError> {
	let auth = auth(&origin)?;
	let access = auth
		.authorize(request.headers(), AccessPolicy::Editor, true)
		.await?;
	let b = body(request, Shape::DateiPatch).await?;
	let mut d = queries::dateien(&auth.pool, None, Some(id))
		.await?
		.pop()
		.filter(|d| d.event_id.is_some() && d.kind == "sonstiges")
		.ok_or_else(|| fail(StatusCode::NOT_FOUND, "Die Datei wurde nicht gefunden."))?;
	let description = b["description"]
		.as_str()
		.map(|v| optional(v, 500, "Die Beschreibung ist zu lang."))
		.transpose()?;
	if b["rowversion"].as_i64().is_some_and(|v| v != d.row_version) {
		return Err(fail(StatusCode::CONFLICT, CONFLICT));
	}
	if let Some(description) = description
		&& description.as_deref().unwrap_or_default()
			!= d.description.as_deref().unwrap_or_default()
	{
		let n=sqlx::query(r#"UPDATE dateien SET "Description"=$2,"RowVersion"="RowVersion"+1 WHERE "Id"=$1 AND "RowVersion"=$3 AND "DeletedAt" IS NULL"#).bind(id).bind(&description).bind(d.row_version).execute(&auth.pool).await?.rows_affected();
		if n == 0 {
			return Err(fail(StatusCode::CONFLICT, CONFLICT));
		}
		d.description = description;
		d.row_version += 1;
	}
	Ok(access.respond(Json(json!({"datei":d.summary(Value::Null)})).into_response()))
}
