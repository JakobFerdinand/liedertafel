use archive_server::ai::{Options, Price, cost_micro_eur, month_key};
use chrono::{TimeZone, Utc};

#[test]
fn decimal_money_rounds_each_call_and_never_uses_float() {
	let price = Price::new("0.83", "4.95").unwrap();
	assert_eq!(cost_micro_eur(&price, 100, 10).unwrap(), 133);
	assert_eq!(cost_micro_eur(&price, 200, 20).unwrap(), 265);
	assert_eq!(cost_micro_eur(&price, 300, 30).unwrap(), 398);
	assert_eq!(
		cost_micro_eur(
			&Price::new("0.1056", "0.528").unwrap(),
			1_000_000,
			1_000_000
		)
		.unwrap(),
		633_600
	);
	assert!(
		cost_micro_eur(
			&Price::new("79228162514264337593543950335", "1").unwrap(),
			2,
			0
		)
		.is_err()
	);
	assert_eq!(Options::default().cap_micro_eur().unwrap(), 15_000_000);
}

#[test]
fn month_is_vienna_calendar_including_summer_and_winter() {
	for (instant, expected) in [
		((2026, 10, 31, 23, 30), "2026-11"),
		((2026, 10, 31, 22, 59), "2026-10"),
		((2026, 6, 30, 22, 1), "2026-07"),
		((2026, 6, 30, 21, 59), "2026-06"),
	] {
		let (y, m, d, h, n) = instant;
		assert_eq!(
			month_key(Utc.with_ymd_and_hms(y, m, d, h, n, 0).unwrap()),
			expected
		);
	}
}

#[test]
fn shipped_price_cap_and_long_context_bound_match_frozen_configuration_and_infrastructure() {
	let value: serde_json::Value =
		serde_json::from_str(include_str!("../../../..//backend/appsettings.json")).unwrap();
	let options = Options::shipped(false);
	assert_eq!(
		options.max_input_tokens,
		value["Archive"]["Ai"]["MaxInputTokensPerCall"]
			.as_i64()
			.unwrap()
	);
	assert_eq!(options.max_output_tokens, 6000);
	assert!((1..=272000).contains(&options.max_input_tokens));
	let price = options.find_price("GPT-6-LUNA").unwrap();
	assert_eq!(price.input.to_string(), "0.1056");
	assert_eq!(price.output.to_string(), "0.528");
	assert!(options.find_price("gpt-6-sol").is_none());
	assert!(options.find_price("scripted").is_none());
	assert!(Options::shipped(true).find_price("scripted").is_some());
	let configured = &value["Archive"]["Ai"]["Models"]["gpt-6-luna"];
	assert_eq!(
		price.input,
		configured["InputPricePerMillionEur"]
			.to_string()
			.parse()
			.unwrap()
	);
	assert_eq!(
		price.output,
		configured["OutputPricePerMillionEur"]
			.to_string()
			.parse()
			.unwrap()
	);
	let bicep = include_str!("../../../../../../infrastructure/archive/main.bicep");
	let parameter = regex::Regex::new(r"(?m)^param aiChatDeploymentName string = '([^']+)'")
		.unwrap()
		.captures(bicep)
		.unwrap();
	assert!(options.find_price(&parameter[1]).is_some());
	assert!(regex::Regex::new(r"(?m)^resource \w+ 'Microsoft\.CognitiveServices/accounts/deployments@[^']+' = \{\s*parent: \w+\s*name: aiChatDeploymentName\s*$").unwrap().is_match(bicep));
}
