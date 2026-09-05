<script lang="ts">
	let user = $state<{ name?: string; avatar?: string } | null>(null);

	$effect(() => {
		let cancelled = false;
		const controller = new AbortController();
		fetch("/.auth/me", { signal: controller.signal, cache: "no-store" })
			.then((res) => (res.ok ? res.json() : null))
			.then((data) => {
				if (cancelled) {
					return;
				}
				const principal = data?.clientPrincipal;
				if (!principal) {
					return;
				}
				const avatarClaim = principal.claims?.find(
					(c: { typ?: string }) => c.typ === "avatar",
				);
				user = {
					name: typeof principal.userDetails === "string" ? principal.userDetails : undefined,
					avatar: avatarClaim ? avatarClaim.val : undefined,
				};
			})
			.catch(() => {
				user = null;
			});
		return () => {
			cancelled = true;
			controller.abort();
		};
	});
</script>

<nav class="navbar" aria-label="Hauptnavigation">
	<div class="container navbar-inner">
		<a class="navbar-brand" href="/">Liedertafel Dashboard</a>
		<a href="/">Übersicht</a>
		<a href="/sessions">Sitzungen</a>
		<a href="https://liedertafel.at" rel="noreferrer" target="_blank">Zur Website</a>
		{#if user}
			<span class="navbar-user">
				{#if user.avatar}
					<img class="navbar-avatar" src={user.avatar} alt="" />
				{/if}
				{user.name}
			</span>
		{/if}
		<a href="/.auth/logout">Abmelden</a>
	</div>
</nav>