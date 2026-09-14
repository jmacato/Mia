// SPDX-License-Identifier: MIT
use flate2::read::GzDecoder;
use rayon::prelude::*;
use serde::Deserialize;
use std::collections::HashMap;
use std::fs::File;
use std::io::BufReader;

const BIN_COUNT: usize = 2048;
const LOOP_HZ: f64 = 1.1019;
const MACHINE_HZ: f64 = 13_000_000.0;

#[derive(Deserialize)]
struct Snapshot {
    window: Window,
    #[allow(dead_code)]
    clock: Clock,
    capture: Capture,
    addresses: Vec<AddressSnapshot>,
}

fn goertzel(signal: &[f64], freq_hz: f64, sample_rate: f64) -> (f64, f64, f64) {
    let n = signal.len();
    if n == 0 { return (0.0, 0.0, 0.0); }
    let k = freq_hz * n as f64 / sample_rate;
    let omega = 2.0 * std::f64::consts::PI * k / n as f64;
    let cos_w = omega.cos();
    let sin_w = omega.sin();
    let coeff = 2.0 * cos_w;
    let mut s_prev = 0.0_f64;
    let mut s_prev2 = 0.0_f64;
    for &s in signal.iter().take(n) {
        let s_new = s + coeff * s_prev - s_prev2;
        s_prev2 = s_prev;
        s_prev = s_new;
   }
    let real = s_prev - s_prev2 * cos_w;
    let imag = s_prev2 * sin_w;
    ((real * real + imag * imag).sqrt(), imag.atan2(real), real * real + imag * imag)
}

fn cyclic_resultant(phases: &[f64]) -> f64 {
    if phases.is_empty() { return 0.0; }
    let sum_sin: f64 = phases.iter().map(|p| p.sin()).sum();
    let sum_cos: f64 = phases.iter().map(|p| p.cos()).sum();
    ((sum_sin * sum_sin + sum_cos * sum_cos).sqrt()) / phases.len() as f64
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Window {
    start_cycle: i64,
    end_cycle_exclusive: i64,
    duration_cycles: i64,
    bin_count: i32,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Clock {
    machine_cycle_domain: String,
    machine_cycles_per_second: i64,
    avr_cycles_per_second: i64,
    avr_to_machine_numerator: i64,
    avr_to_machine_denominator: i64,
    avr_to_machine_offset: i64,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Capture {
    stopped: bool,
    stopped_at_cycle: Option<i64>,
}

#[derive(Deserialize)]
struct AddressSnapshot {
    bus: String,
    address: u32,
    size: u8,
    operations: Vec<OperationSnapshot>,
}

#[derive(Deserialize)]
struct OperationSnapshot {
    operation: String,
    count: i64,
    #[serde(rename = "initialHeldValue")]
    initial_held_value: Option<u64>,
    #[serde(rename = "timeBins")]
    time_bins: Vec<TimeBin>,
}

type TimeBin = (
    usize, // index
    i64,   // event_count
    i64,   // change_count
    u64,   // last_value
    i64,   // value_overflow_count
    Vec<(u64, i64)>, // per-value counts
);

#[derive(Clone, serde::Serialize)]
struct CandidateResult {
    id: String,
    bus: String,
    op: String,
    addr_hex: String,
    service_total: f64,
    control_total: f64,
    power_ratio_db: f64,
    fundamental_ratio: f64,
    cramers_v: f64,
    best_bit_match_pct: f64,
    best_match_desc: String,
    dominant_value_snr: f64,
    phase_resultant: f64,
}

fn load_snapshot(path: &str) -> Snapshot {
    let file = File::open(path).unwrap_or_else(|e| panic!("open {}: {}", path, e));
    let decoder = GzDecoder::new(BufReader::new(file));
    serde_json::from_reader(decoder).unwrap_or_else(|e| panic!("parse {}: {}", path, e))
}

fn collect_files(dir: &str, keyword: &str) -> Vec<String> {
    std::fs::read_dir(dir)
        .unwrap()
        .filter_map(|e| e.ok())
        .map(|e| e.path().to_string_lossy().to_string())
        .filter(|p| {
            let name = std::path::Path::new(p)
                .file_name()
                .map(|n| n.to_string_lossy().to_string())
                .unwrap_or_default();
            name.ends_with(".json.gz") && name.contains(keyword)
        })
        .collect()
}

fn expand_events(bins: &[TimeBin]) -> Vec<f64> {
    let mut out = vec![0.0_f64; BIN_COUNT];
    for bin in bins {
        if bin.0 < BIN_COUNT {
            out[bin.0] = bin.1 as f64;
        }
    }
    out
}

fn expand_changes(bins: &[TimeBin]) -> Vec<f64> {
    let mut out = vec![0.0_f64; BIN_COUNT];
    for bin in bins {
        if bin.0 < BIN_COUNT {
            out[bin.0] = bin.2 as f64;
        }
    }
    out
}

fn expand_held_values(op: &OperationSnapshot) -> Vec<Option<u64>> {
    let mut held = vec![Option::<u64>::None; BIN_COUNT];
    let mut current = op.initial_held_value;
    let mut bin_map = HashMap::new();
    for bin in &op.time_bins {
        bin_map.insert(bin.0, bin.3);
    }
    for b in 0..BIN_COUNT {
        if let Some(&v) = bin_map.get(&b) { current = Some(v); }
        held[b] = current;
    }
    held
}

/// A run is one capture (service or control) for one pair.
#[derive(Clone)]
struct RunData {
    pair_id: usize,
    is_service: bool,
    events: Vec<f64>,
    changes: Vec<f64>,
    held: Vec<Option<u64>>,
    duration_secs: f64,
}

fn score_candidate(
    id: &str,
    bus: &str, op: &str, addr: u32, _size: u8,
    runs: &[Option<RunData>],
) -> Option<CandidateResult> {
    let service_runs: Vec<&RunData> = runs.iter().filter_map(|r| r.as_ref()).filter(|r| r.is_service).collect();
    let control_runs: Vec<&RunData> = runs.iter().filter_map(|r| r.as_ref()).filter(|r| !r.is_service).collect();
    if service_runs.is_empty() { return None; }

    let service_total: f64 = service_runs.iter().map(|r| r.events.iter().sum::<f64>()).sum();
    let control_total: f64 = control_runs.iter().map(|r| r.events.iter().sum::<f64>()).sum();
    if service_total < 20.0 { return None; }

    // Spectral analysis at loop frequency
    let svc_power: f64 = service_runs.iter()
        .map(|r| goertzel(&r.events, LOOP_HZ, MACHINE_HZ).2)
        .sum::<f64>() / service_runs.len().max(1) as f64;
    let ctl_power: f64 = control_runs.iter()
        .map(|r| goertzel(&r.events, LOOP_HZ, MACHINE_HZ).2)
        .sum::<f64>() / control_runs.len().max(1) as f64;

    let power_ratio_db = if ctl_power > 1e-15 {
        10.0 * (svc_power / ctl_power).log10()
    } else if svc_power > 1e-15 { 60.0 } else { 0.0 };

    // Fundamental ratio (fraction of total variance explained by loop freq)
    let total_var: f64 = service_runs.iter()
        .map(|r| {
            let mean = r.events.iter().sum::<f64>() / BIN_COUNT as f64;
            r.events.iter().map(|x| (x - mean).powi(2)).sum::<f64>()
        })
        .sum::<f64>() / service_runs.len().max(1) as f64;
    let fundamental_ratio = if total_var > 1e-15 { svc_power / total_var } else { 0.0 };

    // Phase coherence across service runs
    let phases: Vec<f64> = service_runs.iter()
        .map(|r| goertzel(&r.events, LOOP_HZ, MACHINE_HZ).1)
        .collect();
    let phase_resultant = cyclic_resultant(&phases);

    // Categorical + bit-level state matching
    let duration = service_runs[0].duration_secs;
    let loop_period = 1.0 / LOOP_HZ;
    let (cramers_v, best_pct, best_desc) = score_categorical(
        &service_runs, &control_runs, duration, loop_period);

    // Dominant value spectral SNR
    let dom_snr = score_dominant_value_snr(&service_runs, id, duration);

    Some(CandidateResult {
        id: id.to_string(),
        bus: bus.to_string(),
        op: op.to_string(),
        addr_hex: format!("0x{:x}", addr),
        service_total,
        control_total,
        power_ratio_db,
        fundamental_ratio,
        cramers_v,
        best_bit_match_pct: best_pct,
        best_match_desc: best_desc,
        dominant_value_snr: dom_snr,
        phase_resultant,
    })
}

/// Expected 6-state LED patterns (bit0=blue, bit1=green, bit2=red).
/// Order: blue, blue+red, blue+green, off, red, green.
const EXPECTED_PATTERNS: [(&str, u8); 3] = [
    ("blue",   0b001),
    ("green",  0b010),
    ("red",    0b100),
];
const STATE_MASKS: [u8; 6] = [0b001, 0b101, 0b011, 0b000, 0b100, 0b010];

fn score_categorical(
    service: &[&RunData], _control: &[&RunData],
    duration_secs: f64, loop_period: f64,
) -> (f64, f64, String) {
    let mut contig = [[0_u64; 256]; 6];
    for run in service {
        for (b, hv) in run.held.iter().enumerate() {
            let t = (b as f64 + 0.5) * duration_secs / BIN_COUNT as f64;
            let phase_bin = ((t / loop_period * 6.0).floor() as usize) % 6;
            if let Some(v) = hv {
                contig[phase_bin][(*v as usize).min(255)] += 1;
            }
        }
    }

    let row_sums: Vec<u64> = contig.iter().map(|r| r.iter().sum()).collect();
    let mut col_sums = [0_u64; 256];
    for r in &contig {
        for (j, &v) in r.iter().enumerate() { col_sums[j] += v; }
    }
    let total: u64 = row_sums.iter().sum();
    if total == 0 { return (0.0, 0.0, String::new()); }
    let mut chi_sq = 0.0_f64;
    for i in 0..6 {
        for j in 0..256 {
            let expected = row_sums[i] as f64 * col_sums[j] as f64 / total as f64;
            if expected > 0.5 {
                chi_sq += (contig[i][j] as f64 - expected).powi(2) / expected;
            }
        }
    }
    let cramer_v = (chi_sq / (total as f64 * 5.0)).sqrt();

    let mut best_pct = 0.0_f64;
    let mut best_desc = String::new();

    for bit in 0..8_u64 {
        let mut bit_profile = [0_u64; 6];
        for run in service {
            for (b, hv) in run.held.iter().enumerate() {
                let t = (b as f64 + 0.5) * duration_secs / BIN_COUNT as f64;
                let phase_bin = ((t / loop_period * 6.0).floor() as usize) % 6;
                if let Some(v) = hv {
                    if (*v >> bit) & 1 == 1 { bit_profile[phase_bin] += 1; }
                }
            }
        }
        let bit_total: u64 = bit_profile.iter().sum();
        if bit_total < 50 { continue; }
        let threshold = bit_total / 12;
        let observed: Vec<bool> = bit_profile.iter().map(|&x| x > threshold).collect();
        let observed_inv: Vec<bool> = observed.iter().map(|x| !*x).collect();

        for (led_name, mask) in EXPECTED_PATTERNS.iter() {
            let expected: Vec<bool> = STATE_MASKS.iter().map(|m| (m & mask) != 0).collect();
            for (obs, suffix) in [(&observed, ""), (&observed_inv, "_inv")] {
                let matches = obs.iter().zip(expected.iter()).filter(|(o, e)| o == e).count();
                let pct = matches as f64 / 6.0 * 100.0;
                if pct > best_pct {
                    best_desc = format!("bit{}{}_vs_{}", bit, suffix, led_name);
                    best_pct = pct;
                }
            }
        }
    }

    (cramer_v, best_pct, best_desc)
}

fn score_dominant_value_snr(service: &[&RunData], _id: &str, _duration: f64) -> f64 {
    // For each candidate, find the most common non-zero value across service runs
    let mut value_counts: HashMap<u64, u64> = HashMap::new();
    for run in service {
        for hv in &run.held {
            if let Some(v) = hv {
                if *v != 0 { *value_counts.entry(*v).or_insert(0) += 1; }
            }
        }
    }
    let mut top_values: Vec<(u64, u64)> = value_counts.into_iter().collect();
    top_values.sort_by_key(|(_, c)| std::cmp::Reverse(*c));

    let mut best_snr = 0.0_f64;
    for (val, _) in top_values.iter().take(5) {
        // Build per-bin count series for this specific held-value
        let mut val_series = vec![0.0_f64; BIN_COUNT];
        for run in service {
            // Rebuild from time_bins would need original data; use held as proxy:
            // count bins where this value is held
            for (b, hv) in run.held.iter().enumerate() {
                if *hv == Some(*val) { val_series[b] += 1.0; }
            }
        }
        let (_, _, power) = goertzel(&val_series, LOOP_HZ, MACHINE_HZ);
        let mean = val_series.iter().sum::<f64>() / BIN_COUNT as f64;
        let var_val: f64 = val_series.iter().map(|x| (x - mean).powi(2)).sum();
        let snr = if var_val > 1e-15 { power / var_val } else { 0.0 };
        if snr > best_snr { best_snr = snr; }
    }
    best_snr
}

fn main() {
    let args: Vec<String> = std::env::args().collect();
    if args.len() < 2 {
        eprintln!("Usage: rust-led-correlator <capture-directory>");
        std::process::exit(64);
    }
    let dir = &args[1];

    let mut service_files = collect_files(dir, "service");
    let mut control_files = collect_files(dir, "control");
    service_files.sort();
    control_files.sort();
    assert_eq!(service_files.len(), control_files.len());
    let pair_count = service_files.len();

    eprintln!("Loading captures...");

    struct LoadedRun {
        pair_id: usize, is_service: bool,
        snapshot: Snapshot,
        lookup: HashMap<String, (usize, usize)>,
        duration_secs: f64,
    }

    let mut loaded: Vec<LoadedRun> = Vec::new();
    for (i, path) in service_files.iter().chain(control_files.iter()).enumerate() {
        let pair_id = i / 2;
        let is_service = i % 2 == 0;
        let snap = load_snapshot(path);
        let duration = snap.window.duration_cycles as f64
            / snap.clock.machine_cycles_per_second as f64;
        let mut lookup = HashMap::new();
        for (ai, addr) in snap.addresses.iter().enumerate() {
            for (oi, op) in addr.operations.iter().enumerate() {
                let id = format!("{}:{}:0x{:x}:size={}", addr.bus, op.operation, addr.address, addr.size);
                lookup.insert(id, (ai, oi));
            }
        }
        let stopped_at = snap.capture.stopped_at_cycle;
        assert!(snap.capture.stopped && stopped_at == Some(snap.window.end_cycle_exclusive));
        loaded.push(LoadedRun { pair_id, is_service, snapshot: snap, lookup, duration_secs: duration });
    }

    let mut inventory = HashMap::new();
    for run in &loaded {
        for addr in &run.snapshot.addresses {
            for op in &addr.operations {
                let id = format!("{}:{}:0x{:x}:size={}", addr.bus, op.operation, addr.address, addr.size);
                inventory.entry(id).or_insert((
                    addr.bus.clone(), op.operation.clone(), addr.address, addr.size
                ));
            }
        }
    }
    let sorted_ids: Vec<String> = {
        let mut v: Vec<_> = inventory.keys().cloned().collect();
        v.sort();
        v
    };
    eprintln!("Inventory: {} candidates", sorted_ids.len());

    eprintln!("Scoring...");
    let results: Vec<CandidateResult> = sorted_ids.par_iter()
        .filter_map(|id| {
            let mut runs: [Option<RunData>; 6] = Default::default();
            for (ri, lr) in loaded.iter().enumerate() {
                if let Some(&(ai, oi)) = lr.lookup.get(id.as_str()) {
                    let addr = &lr.snapshot.addresses[ai];
                    let op = &addr.operations[oi];
                    runs[ri] = Some(RunData {
                        pair_id: lr.pair_id,
                        is_service: lr.is_service,
                        events: expand_events(&op.time_bins),
                        changes: expand_changes(&op.time_bins),
                        held: expand_held_values(op),
                        duration_secs: lr.duration_secs,
                    });
                } else {
                    runs[ri] = Some(RunData {
                        pair_id: lr.pair_id,
                        is_service: lr.is_service,
                        events: vec![0.0; BIN_COUNT],
                        changes: vec![0.0; BIN_COUNT],
                        held: vec![None; BIN_COUNT],
                        duration_secs: lr.duration_secs,
                    });
                }
            }
            let meta = inventory.get(id).unwrap();
            score_candidate(id, &meta.0, &meta.1, meta.2, meta.3, &runs)
        })
        .collect();

    eprintln!("Scored {}", results.len());
    let mut ranked: Vec<&CandidateResult> = results.iter().collect();
    ranked.sort_by(|a, b| {
        let sa = a.power_ratio_db + a.dominant_value_snr * 100.0 + a.phase_resultant * 20.0;
        let sb = b.power_ratio_db + b.dominant_value_snr * 100.0 + b.phase_resultant * 20.0;
        sb.partial_cmp(&sa).unwrap_or(std::cmp::Ordering::Equal)
    });

    println!("{{");
    println!("  \"schema\": \"mia-rust-led-correlation-v1\",");
    println!("  \"inventory\": {},", sorted_ids.len());
    println!("  \"scored\": {},", results.len());
    println!("  \"top\": [");
    for (i, r) in ranked.iter().take(50).enumerate() {
        let j = serde_json::to_string(r).unwrap();
        let comma = if i < ranked.len().min(50) - 1 { "," } else { "" };
        println!("    {}{}", j, comma);
    }
    println!("  ]");
    println!("}}");
}
