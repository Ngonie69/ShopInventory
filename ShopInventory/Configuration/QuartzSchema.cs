using Npgsql;

namespace ShopInventory.Configuration;

/// <summary>
/// Ensures the Quartz ADO.NET (AdoJobStore) tables exist in Postgres before the scheduler starts.
/// Runs the canonical Quartz 3.x PostgreSQL schema idempotently (CREATE TABLE IF NOT EXISTS), plus
/// seeds the per-scheduler row locks required by the clustered store. Executed at startup instead
/// of via an EF migration so it works regardless of whether the API and Web apps share a database.
/// NOTE: validate against the target Postgres in staging — this must match the Quartz version's
/// official database/tables/tables_postgres.sql.
/// </summary>
public static class QuartzSchema
{
    public static async Task EnsureAsync(string connectionString, string schedulerName, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using (var command = new NpgsqlCommand(SchemaSql, connection))
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        // Clustered locking requires a lock row per scheduler name.
        await using (var command = new NpgsqlCommand(
            "INSERT INTO qrtz_locks (sched_name, lock_name) VALUES (@s, 'TRIGGER_ACCESS'), (@s, 'STATE_ACCESS') ON CONFLICT DO NOTHING",
            connection))
        {
            command.Parameters.AddWithValue("s", schedulerName);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private const string SchemaSql = """
CREATE TABLE IF NOT EXISTS qrtz_job_details (
  sched_name text NOT NULL,
  job_name text NOT NULL,
  job_group text NOT NULL,
  description text NULL,
  job_class_name text NOT NULL,
  is_durable bool NOT NULL,
  is_nonconcurrent bool NOT NULL,
  is_update_data bool NOT NULL,
  requests_recovery bool NOT NULL,
  job_data bytea NULL,
  PRIMARY KEY (sched_name, job_name, job_group)
);

CREATE TABLE IF NOT EXISTS qrtz_triggers (
  sched_name text NOT NULL,
  trigger_name text NOT NULL,
  trigger_group text NOT NULL,
  job_name text NOT NULL,
  job_group text NOT NULL,
  description text NULL,
  next_fire_time bigint NULL,
  prev_fire_time bigint NULL,
  priority integer NULL,
  trigger_state text NOT NULL,
  trigger_type text NOT NULL,
  start_time bigint NOT NULL,
  end_time bigint NULL,
  calendar_name text NULL,
  misfire_instr smallint NULL,
  misfire_orig_fire_time bigint NULL,
  job_data bytea NULL,
  PRIMARY KEY (sched_name, trigger_name, trigger_group),
  FOREIGN KEY (sched_name, job_name, job_group) REFERENCES qrtz_job_details (sched_name, job_name, job_group)
);

ALTER TABLE qrtz_triggers
  ADD COLUMN IF NOT EXISTS misfire_orig_fire_time bigint NULL;

CREATE TABLE IF NOT EXISTS qrtz_simple_triggers (
  sched_name text NOT NULL,
  trigger_name text NOT NULL,
  trigger_group text NOT NULL,
  repeat_count bigint NOT NULL,
  repeat_interval bigint NOT NULL,
  times_triggered bigint NOT NULL,
  PRIMARY KEY (sched_name, trigger_name, trigger_group),
  FOREIGN KEY (sched_name, trigger_name, trigger_group) REFERENCES qrtz_triggers (sched_name, trigger_name, trigger_group) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS qrtz_cron_triggers (
  sched_name text NOT NULL,
  trigger_name text NOT NULL,
  trigger_group text NOT NULL,
  cron_expression text NOT NULL,
  time_zone_id text NULL,
  PRIMARY KEY (sched_name, trigger_name, trigger_group),
  FOREIGN KEY (sched_name, trigger_name, trigger_group) REFERENCES qrtz_triggers (sched_name, trigger_name, trigger_group) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS qrtz_simprop_triggers (
  sched_name text NOT NULL,
  trigger_name text NOT NULL,
  trigger_group text NOT NULL,
  str_prop_1 text NULL,
  str_prop_2 text NULL,
  str_prop_3 text NULL,
  int_prop_1 int NULL,
  int_prop_2 int NULL,
  long_prop_1 bigint NULL,
  long_prop_2 bigint NULL,
  dec_prop_1 numeric NULL,
  dec_prop_2 numeric NULL,
  bool_prop_1 bool NULL,
  bool_prop_2 bool NULL,
  time_zone_id text NULL,
  PRIMARY KEY (sched_name, trigger_name, trigger_group),
  FOREIGN KEY (sched_name, trigger_name, trigger_group) REFERENCES qrtz_triggers (sched_name, trigger_name, trigger_group) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS qrtz_blob_triggers (
  sched_name text NOT NULL,
  trigger_name text NOT NULL,
  trigger_group text NOT NULL,
  blob_data bytea NULL,
  PRIMARY KEY (sched_name, trigger_name, trigger_group),
  FOREIGN KEY (sched_name, trigger_name, trigger_group) REFERENCES qrtz_triggers (sched_name, trigger_name, trigger_group) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS qrtz_calendars (
  sched_name text NOT NULL,
  calendar_name text NOT NULL,
  calendar bytea NOT NULL,
  PRIMARY KEY (sched_name, calendar_name)
);

CREATE TABLE IF NOT EXISTS qrtz_paused_trigger_grps (
  sched_name text NOT NULL,
  trigger_group text NOT NULL,
  PRIMARY KEY (sched_name, trigger_group)
);

CREATE TABLE IF NOT EXISTS qrtz_fired_triggers (
  sched_name text NOT NULL,
  entry_id text NOT NULL,
  trigger_name text NOT NULL,
  trigger_group text NOT NULL,
  instance_name text NOT NULL,
  fired_time bigint NOT NULL,
  sched_time bigint NOT NULL,
  priority integer NOT NULL,
  state text NOT NULL,
  job_name text NULL,
  job_group text NULL,
  is_nonconcurrent bool NULL,
  requests_recovery bool NULL,
  PRIMARY KEY (sched_name, entry_id)
);

CREATE TABLE IF NOT EXISTS qrtz_scheduler_state (
  sched_name text NOT NULL,
  instance_name text NOT NULL,
  last_checkin_time bigint NOT NULL,
  checkin_interval bigint NOT NULL,
  PRIMARY KEY (sched_name, instance_name)
);

CREATE TABLE IF NOT EXISTS qrtz_locks (
  sched_name text NOT NULL,
  lock_name text NOT NULL,
  PRIMARY KEY (sched_name, lock_name)
);

CREATE INDEX IF NOT EXISTS idx_qrtz_t_next_fire_time ON qrtz_triggers (sched_name, next_fire_time);
CREATE INDEX IF NOT EXISTS idx_qrtz_t_state ON qrtz_triggers (sched_name, trigger_state);
CREATE INDEX IF NOT EXISTS idx_qrtz_ft_trig_inst_name ON qrtz_fired_triggers (sched_name, instance_name);
CREATE INDEX IF NOT EXISTS idx_qrtz_ft_inst_job_req_rcvry ON qrtz_fired_triggers (sched_name, instance_name, requests_recovery);
""";
}
