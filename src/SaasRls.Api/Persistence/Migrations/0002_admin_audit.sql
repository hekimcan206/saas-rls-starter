-- Cross-tenant operations. The saas_admin login role has no table privileges; it can only EXECUTE the functions below.
-- They are SECURITY DEFINER functions owned by saas_admin_definer (NOLOGIN, BYPASSRLS), and every one of them writes
-- an audit row in the same transaction before it returns data, so there is no unaudited path across tenants.

CREATE TABLE audit.admin_actions (
    id         bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    at         timestamptz NOT NULL DEFAULT clock_timestamp(),
    db_role    text        NOT NULL,  -- session_user: the login that called (current_user is the definer inside)
    operator   text        NOT NULL,  -- the human operator, from the validated admin token
    action     text        NOT NULL,
    tenant_id  uuid,
    reason     text        NOT NULL,
    detail     jsonb       NOT NULL DEFAULT '{}'::jsonb
);

-- Append-only, including for the table owner. (A superuser can still drop the trigger: see docs/threat-model.md.)
CREATE FUNCTION audit.forbid_change() RETURNS trigger
    LANGUAGE plpgsql
    AS $$ BEGIN RAISE EXCEPTION 'audit.admin_actions is append-only' USING ERRCODE = '42501'; END $$;
CREATE TRIGGER admin_actions_no_update_delete BEFORE UPDATE OR DELETE ON audit.admin_actions
    FOR EACH ROW EXECUTE FUNCTION audit.forbid_change();
CREATE TRIGGER admin_actions_no_truncate BEFORE TRUNCATE ON audit.admin_actions
    FOR EACH STATEMENT EXECUTE FUNCTION audit.forbid_change();

GRANT USAGE ON SCHEMA app, admin, audit TO saas_admin_definer;
GRANT CREATE ON SCHEMA admin TO saas_admin_definer;
GRANT SELECT, INSERT, DELETE ON app.tenants, app.customers, app.orders, app.tenant_counters TO saas_admin_definer;
GRANT INSERT, SELECT ON audit.admin_actions TO saas_admin_definer;
GRANT USAGE ON SCHEMA admin, audit TO saas_admin;
GRANT SELECT ON audit.admin_actions TO saas_admin;

-- From here on the objects are created by, and owned by, the definer role.
SET LOCAL ROLE saas_admin_definer;

-- Every definer function pins search_path: otherwise a caller who can create objects in a schema on its search_path
-- could make "count" or "=" resolve to its own function and run it with the definer's rights.
CREATE FUNCTION admin.record(p_action text, p_tenant uuid, p_reason text, p_detail jsonb) RETURNS void
    LANGUAGE plpgsql
    SET search_path = pg_catalog, pg_temp
    AS $$
    DECLARE
        v_operator text := nullif(btrim(current_setting('admin.operator', true)), '');
    BEGIN
        IF v_operator IS NULL THEN
            RAISE EXCEPTION 'admin.operator is not set: cross-tenant actions must name the human operator'
                USING ERRCODE = '28000';
        END IF;
        IF p_reason IS NULL OR length(btrim(p_reason)) < 10 THEN
            RAISE EXCEPTION 'a reason of at least 10 characters is required for cross-tenant actions'
                USING ERRCODE = '22023';
        END IF;
        INSERT INTO audit.admin_actions (db_role, operator, action, tenant_id, reason, detail)
        VALUES (session_user, v_operator, p_action, p_tenant, btrim(p_reason), coalesce(p_detail, '{}'::jsonb));
    END
    $$;

CREATE FUNCTION admin.create_tenant(p_name text, p_plan text, p_reason text) RETURNS uuid
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path = pg_catalog, pg_temp
    AS $$
    DECLARE
        v_id uuid := gen_random_uuid();
    BEGIN
        INSERT INTO app.tenants (id, name, plan) VALUES (v_id, p_name, coalesce(p_plan, 'free'));
        PERFORM admin.record('tenant.create', v_id, p_reason, jsonb_build_object('name', p_name, 'plan', coalesce(p_plan, 'free')));
        RETURN v_id;
    END
    $$;

CREATE FUNCTION admin.tenant_usage(p_reason text)
    RETURNS TABLE (tenant_id uuid, name text, plan text, customers bigint, orders bigint, revenue_cents bigint)
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path = pg_catalog, pg_temp
    AS $$
    BEGIN
        PERFORM admin.record('tenant.usage', NULL, p_reason, '{}'::jsonb);
        RETURN QUERY
            SELECT t.id, t.name, t.plan,
                   (SELECT count(*) FROM app.customers c WHERE c.tenant_id = t.id),
                   (SELECT count(*) FROM app.orders o WHERE o.tenant_id = t.id),
                   (SELECT coalesce(sum(o.total_cents), 0)::bigint FROM app.orders o WHERE o.tenant_id = t.id)
            FROM app.tenants t
            ORDER BY t.name, t.id;
    END
    $$;

-- Support access to one tenant's data. Audited with the row count actually returned.
CREATE FUNCTION admin.tenant_orders(p_tenant uuid, p_reason text, p_limit integer)
    RETURNS TABLE (id uuid, customer_id uuid, number bigint, status text, total_cents bigint, created_at timestamptz)
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path = pg_catalog, pg_temp
    AS $$
    DECLARE
        v_limit integer := least(greatest(coalesce(p_limit, 50), 1), 1000);
    BEGIN
        RETURN QUERY
            SELECT o.id, o.customer_id, o.number, o.status, o.total_cents, o.created_at
            FROM app.orders o
            WHERE o.tenant_id = p_tenant
            ORDER BY o.created_at DESC, o.id
            LIMIT v_limit;
        PERFORM admin.record('tenant.orders.read', p_tenant, p_reason, jsonb_build_object('limit', v_limit));
    END
    $$;

-- Offboarding: deletes the tenant and, through the cascading foreign keys, all of its rows.
CREATE FUNCTION admin.purge_tenant(p_tenant uuid, p_reason text) RETURNS bigint
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path = pg_catalog, pg_temp
    AS $$
    DECLARE
        v_customers bigint;
        v_orders bigint;
    BEGIN
        SELECT count(*) INTO v_customers FROM app.customers WHERE tenant_id = p_tenant;
        SELECT count(*) INTO v_orders FROM app.orders WHERE tenant_id = p_tenant;
        DELETE FROM app.tenants WHERE id = p_tenant;
        IF NOT FOUND THEN
            RAISE EXCEPTION 'tenant % does not exist', p_tenant USING ERRCODE = 'P0002';
        END IF;
        PERFORM admin.record('tenant.purge', p_tenant, p_reason,
            jsonb_build_object('customers', v_customers, 'orders', v_orders));
        RETURN v_customers + v_orders;
    END
    $$;

-- Granted by the owner (a GRANT by a non-owner is only a warning, and would silently grant nothing). Default privileges
-- already revoke PUBLIC's EXECUTE; repeat it explicitly so the intent survives a changed default.
REVOKE EXECUTE ON ALL FUNCTIONS IN SCHEMA admin FROM PUBLIC;
GRANT EXECUTE ON FUNCTION admin.create_tenant(text, text, text), admin.tenant_usage(text),
    admin.tenant_orders(uuid, text, integer), admin.purge_tenant(uuid, text) TO saas_admin;

RESET ROLE;
