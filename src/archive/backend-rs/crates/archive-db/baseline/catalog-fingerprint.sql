-- PG17 semantic snapshot for safe adoption of the frozen EF baseline.
-- Evaluate with search_path=pg_catalog. Ownership, ACLs, OIDs, physical dropped
-- column slots, sequence current values and table data are deliberately absent.
SELECT md5(jsonb_build_object(
    'schema_comment', (SELECT obj_description(oid, 'pg_namespace') FROM pg_namespace WHERE nspname='public'),
    'relations', (SELECT jsonb_agg(jsonb_build_array(
        c.relname, c.relkind, c.relpersistence, c.relrowsecurity,
        c.relforcerowsecurity, c.relreplident, c.reloptions, am.amname,
        obj_description(c.oid, 'pg_class'),
        (SELECT jsonb_agg(jsonb_build_array(a.attname,
            format_type(a.atttypid, a.atttypmod), a.attnotnull,
            a.attidentity, a.attgenerated, pg_get_expr(d.adbin, d.adrelid),
            cn.nspname, co.collname, col_description(c.oid, a.attnum))
            ORDER BY a.attnum)
         FROM pg_attribute a LEFT JOIN pg_attrdef d
            ON d.adrelid=a.attrelid AND d.adnum=a.attnum
         LEFT JOIN pg_collation co ON co.oid=a.attcollation
         LEFT JOIN pg_namespace cn ON cn.oid=co.collnamespace
         WHERE a.attrelid=c.oid AND a.attnum>0 AND NOT a.attisdropped),
        (SELECT jsonb_agg(jsonb_build_array(con.conname,
            pg_get_constraintdef(con.oid), con.convalidated,
            obj_description(con.oid, 'pg_constraint')) ORDER BY con.conname COLLATE "C")
         FROM pg_constraint con WHERE con.conrelid=c.oid),
        CASE WHEN c.relkind IN ('v', 'm') THEN pg_get_viewdef(c.oid) END
    ) ORDER BY c.relname COLLATE "C") FROM pg_class c
     JOIN pg_namespace n ON n.oid=c.relnamespace
     LEFT JOIN pg_am am ON am.oid=c.relam
     WHERE n.nspname='public' AND c.relkind IN ('r','p','v','m','f','c')
        AND c.relname <> '_sqlx_migrations'),
    'indexes', (SELECT jsonb_agg(jsonb_build_array(c.relname,
        pg_get_indexdef(c.oid), i.indisvalid, i.indisready,
        obj_description(c.oid, 'pg_class')) ORDER BY c.relname COLLATE "C")
        FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
        JOIN pg_index i ON i.indexrelid=c.oid JOIN pg_class t ON t.oid=i.indrelid
        WHERE n.nspname='public' AND t.relname <> '_sqlx_migrations'),
    'sequences', (SELECT jsonb_agg(jsonb_build_array(c.relname,
        format_type(s.seqtypid, NULL), s.seqstart, s.seqincrement, s.seqmax,
        s.seqmin, s.seqcache, s.seqcycle,
        (SELECT jsonb_agg(jsonb_build_array(t.relname,a.attname,d.deptype))
         FROM pg_depend d JOIN pg_class t ON t.oid=d.refobjid
         JOIN pg_attribute a ON a.attrelid=t.oid AND a.attnum=d.refobjsubid
         WHERE d.classid='pg_class'::regclass AND d.objid=c.oid AND d.deptype IN ('a','i'))
        ) ORDER BY c.relname COLLATE "C")
        FROM pg_sequence s JOIN pg_class c ON c.oid=s.seqrelid
        JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public'),
    'functions', (SELECT jsonb_agg(pg_get_functiondef(p.oid) ORDER BY p.oid::regprocedure::text COLLATE "C")
        FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='public'),
    'triggers', (SELECT jsonb_agg(pg_get_triggerdef(t.oid) ORDER BY c.relname COLLATE "C", t.tgname COLLATE "C")
        FROM pg_trigger t JOIN pg_class c ON c.oid=t.tgrelid JOIN pg_namespace n ON n.oid=c.relnamespace
        WHERE n.nspname='public' AND NOT t.tgisinternal),
    'policies', (SELECT jsonb_agg(jsonb_build_array(c.relname,p.polname,p.polcmd,
        p.polpermissive,pg_get_expr(p.polqual,p.polrelid),pg_get_expr(p.polwithcheck,p.polrelid))
        ORDER BY c.relname COLLATE "C", p.polname COLLATE "C")
        FROM pg_policy p JOIN pg_class c ON c.oid=p.polrelid
        JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public'),
    'rules', (SELECT jsonb_agg(pg_get_ruledef(r.oid) ORDER BY c.relname COLLATE "C", r.rulename COLLATE "C")
        FROM pg_rewrite r JOIN pg_class c ON c.oid=r.ev_class
        JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public'),
    'extensions', (SELECT jsonb_agg(jsonb_build_array(e.extname,e.extversion) ORDER BY e.extname COLLATE "C")
        FROM pg_extension e JOIN pg_namespace n ON n.oid=e.extnamespace WHERE n.nspname='public'),
    'types', (SELECT jsonb_agg(jsonb_build_array(t.typname,t.typtype,format_type(t.typbasetype,t.typtypmod))
        ORDER BY t.typname COLLATE "C") FROM pg_type t JOIN pg_namespace n ON n.oid=t.typnamespace
        WHERE n.nspname='public' AND t.typrelid=0 AND t.typelem=0)
)::text)
