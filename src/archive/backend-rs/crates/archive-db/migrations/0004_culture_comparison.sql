-- Comparison equality must reach explicit secondary keys. Deterministic ICU
-- adds a bytewise tie-break even for canonical/ignorable equivalences. Keep
-- tertiary root ICU distinctions; this does not fold case or rewrite any data.
CREATE COLLATION public.archive_culture (provider = icu, locale = 'und', deterministic = false);
