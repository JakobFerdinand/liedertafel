use super::App;
use serde_json::Value;
use uuid::Uuid;
pub async fn lied(app: &App, title: &str, fields: Value) -> Uuid {
	let id = Uuid::new_v4();
	sqlx::query(r#"INSERT INTO lieder ("Id","Title","Composer","Arranger","Lyricist","VoiceConfiguration","MusicalKey","Language","Lyrics","Occasion","CreatedAt","CreatedByAccountId","UpdatedAt","UpdatedByAccountId","RowVersion") VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,now(),$11,now(),$11,1)"#)
		.bind(id).bind(title).bind(fields["composer"].as_str()).bind(fields["arranger"].as_str()).bind(fields["lyricist"].as_str()).bind(fields["voiceConfiguration"].as_str()).bind(fields["musicalKey"].as_str()).bind(fields["language"].as_str()).bind(fields["lyrics"].as_str()).bind(fields["occasion"].as_str()).bind(Uuid::nil()).execute(&app.pool).await.unwrap();
	id
}
pub async fn datei(app: &App, owner: Uuid, event: bool, kind: &str, content_type: &str) -> Uuid {
	let id = Uuid::new_v4();
	sqlx::query(r#"INSERT INTO dateien ("Id","LiedId","EventId","Kind","IsPartitur","BlobName","ContentType","SizeBytes","OriginalFileName","CreatedByAccountId","CreatedAt","RowVersion") VALUES ($1,$2,$3,$4,false,$5,$6,128,'datei.pdf',$7,now(),1)"#).bind(id).bind((!event).then_some(owner)).bind(event.then_some(owner)).bind(kind).bind(format!("dateien/{id}/x")).bind(content_type).bind(Uuid::nil()).execute(&app.pool).await.unwrap();
	id
}
pub async fn text(app: &App, datei: Uuid, text: &str, status: i32, source: i32) {
	sqlx::query(r#"INSERT INTO extraction_jobs ("DateiId","Status","Text","AttemptCount","TriggeredByAccountId","CreatedAt","UpdatedAt","RowVersion","TextSource") VALUES ($1,$2,$3,0,$4,now(),now(),1,$5)"#).bind(datei).bind(status).bind(text).bind(Uuid::nil()).bind(source).execute(&app.pool).await.unwrap();
}
pub fn titles(body: &Value) -> Vec<&str> {
	body["lieder"]
		.as_array()
		.unwrap()
		.iter()
		.map(|l| l["title"].as_str().unwrap())
		.collect()
}
pub fn query(q: &str) -> String {
	format!(
		"/api/lieder?{}",
		url::form_urlencoded::Serializer::new(String::new())
			.append_pair("q", q)
			.finish()
	)
}

pub async fn event(app: &App) -> Uuid {
	let id = Uuid::new_v4();
	sqlx::query(r#"INSERT INTO events ("Id","Title","Kind","DateYear","DateMonth","DateDay","DateApproximate","CreatedAt","UpdatedAt","CreatedByAccountId","UpdatedByAccountId","RowVersion") VALUES ($1,'Herbstkonzert','concert',2026,11,9,false,now(),now(),$2,$2,1)"#).bind(id).bind(Uuid::nil()).execute(&app.pool).await.unwrap();
	id
}
pub async fn video_copy(app: &App, original: Uuid, copy: Uuid, status: i32) {
	sqlx::query(r#"INSERT INTO video_jobs ("DateiId","Status","PlaybackDateiId","AttemptCount","TriggeredByAccountId","CreatedAt","UpdatedAt","RowVersion") VALUES ($1,$2,$3,0,$4,now(),now(),1)"#).bind(original).bind(status).bind(copy).bind(Uuid::nil()).execute(&app.pool).await.unwrap();
}
pub async fn recording(
	app: &App,
	event: Uuid,
	original: Uuid,
	copy: Option<Uuid>,
	kind: &str,
) -> Uuid {
	let id = Uuid::new_v4();
	sqlx::query(r#"INSERT INTO recordings ("Id","EventId","Label","Kind","DownloadEnabled","PublishedAt","CreatedAt","UpdatedAt","CreatedByAccountId","UpdatedByAccountId","RowVersion","OriginalDateiId","PlaybackDateiId") VALUES ($1,$2,'Erste Fassung',$3,true,now(),now(),now(),$4,$4,1,$5,$6)"#).bind(id).bind(event).bind(kind).bind(Uuid::nil()).bind(original).bind(copy).execute(&app.pool).await.unwrap();
	id
}
