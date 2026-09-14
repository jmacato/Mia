#!/usr/bin/env python3
"""Measure the Mia service-mode LEDs from source video frames.

The analyzer uses ffprobe timestamps (not a reconstructed frame counter), decodes
RGB frames with ffmpeg, and measures fixed, documented LED regions.  All outputs
are deterministic: there is no wall-clock timestamp, random sampling, or image
encoder metadata in the evidence bundle.
"""

from __future__ import annotations

import argparse
import csv
import hashlib
import io
import json
import math
import shutil
import subprocess
import sys
import tempfile
from dataclasses import dataclass
from fractions import Fraction
from pathlib import Path
from typing import Any, BinaryIO, Iterable

import numpy as np
from PIL import Image, ImageDraw, ImageFont


ANALYZER_VERSION = "1.0"
CONTACT_COLUMNS = 6
PHONE_CROP = (70, 130, 245, 198)
OUTPUT_FILES = (
    "source.json",
    "frames.csv",
    "transitions.csv",
    "intervals.csv",
    "cycles.csv",
    "measurements.json",
    "report.md",
    "roi-reference.png",
    "signals.png",
    "contact-sheet.png",
    "six-state-cycles.csv",
)


@dataclass(frozen=True)
class ColorSpec:
    name: str
    channel: str
    channel_index: int
    roi: tuple[int, int, int, int]
    display_rgb: tuple[int, int, int]
    pale_rgb: tuple[int, int, int]
    audit_crop: tuple[int, int, int, int]


COLOR_SPECS = (
    ColorSpec(
        "blue",
        "B",
        2,
        (90, 143, 110, 180),
        (0, 105, 235),
        (218, 235, 255),
        (80, 136, 125, 188),
    ),
    ColorSpec(
        "green",
        "G",
        1,
        (210, 143, 235, 185),
        (0, 150, 55),
        (220, 247, 226),
        (200, 136, 245, 190),
    ),
    ColorSpec(
        "red",
        "R",
        0,
        (210, 143, 235, 185),
        (220, 25, 45),
        (255, 224, 227),
        (200, 136, 245, 190),
    ),
)
SPEC_BY_NAME = {spec.name: spec for spec in COLOR_SPECS}
COLOR_ORDER = {spec.name: index for index, spec in enumerate(COLOR_SPECS)}


def rotate_box_180(
    box: tuple[int, int, int, int], width: int, height: int
) -> tuple[int, int, int, int]:
    """Transform an exclusive-corner source box into phone-upright coordinates."""

    x0, y0, x1, y1 = box
    return (width - x1, height - y1, width - x0, height - y0)


def rounded(value: float | Fraction, places: int = 9) -> float:
    """Return a JSON-friendly, consistently rounded floating-point number."""

    return round(float(value), places)


def format_number(value: float | None, places: int = 6) -> str:
    if value is None:
        return ""
    return f"{value:.{places}f}"


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def run_json(command: list[str]) -> dict[str, Any]:
    try:
        result = subprocess.run(
            command,
            check=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
        )
    except FileNotFoundError as error:
        raise RuntimeError(f"required executable not found: {command[0]}") from error
    except subprocess.CalledProcessError as error:
        detail = error.stderr.strip() or error.stdout.strip() or "no diagnostic"
        raise RuntimeError(f"{' '.join(command)} failed: {detail}") from error
    return json.loads(result.stdout)


def probe_source(video_path: Path) -> tuple[dict[str, Any], list[dict[str, int]], Fraction]:
    probe = run_json(
        [
            "ffprobe",
            "-v",
            "error",
            "-select_streams",
            "v:0",
            "-show_streams",
            "-show_format",
            "-show_frames",
            "-show_entries",
            (
                "stream=codec_name,width,height,pix_fmt,time_base,start_pts,start_time,"
                "duration_ts,duration,avg_frame_rate,r_frame_rate,nb_frames:"
                "format=format_name,duration,size:"
                "frame=best_effort_timestamp,pkt_duration"
            ),
            "-of",
            "json",
            str(video_path),
        ]
    )
    streams = probe.get("streams", [])
    if len(streams) != 1:
        raise RuntimeError(f"expected one selected video stream, found {len(streams)}")
    stream = streams[0]
    width = int(stream["width"])
    height = int(stream["height"])
    time_base = Fraction(stream["time_base"])
    raw_frames = probe.get("frames", [])
    if not raw_frames:
        raise RuntimeError("ffprobe returned no video frames")

    pts_ticks = [int(frame["best_effort_timestamp"]) for frame in raw_frames]
    if any(right <= left for left, right in zip(pts_ticks, pts_ticks[1:])):
        raise RuntimeError("source-frame PTS values are not strictly increasing")
    deltas = np.diff(np.asarray(pts_ticks, dtype=np.int64))
    typical_duration_ticks = int(np.median(deltas)) if len(deltas) else int(raw_frames[0]["pkt_duration"])
    stream_start_ticks = int(stream.get("start_pts", pts_ticks[0]))
    if "duration_ts" in stream:
        stream_end_ticks = stream_start_ticks + int(stream["duration_ts"])
    else:
        stream_end_ticks = pts_ticks[-1] + int(raw_frames[-1].get("pkt_duration", typical_duration_ticks))
    if stream_end_ticks <= pts_ticks[-1]:
        stream_end_ticks = pts_ticks[-1] + int(raw_frames[-1].get("pkt_duration", typical_duration_ticks))

    frame_records: list[dict[str, int]] = []
    for index, pts in enumerate(pts_ticks):
        next_pts = pts_ticks[index + 1] if index + 1 < len(pts_ticks) else stream_end_ticks
        duration_ticks = next_pts - pts
        if duration_ticks <= 0:
            raise RuntimeError(f"invalid source-frame duration at frame {index}")
        frame_records.append(
            {
                "index": index,
                "pts_ticks": pts,
                "duration_ticks": duration_ticks,
            }
        )

    nominal_rate = Fraction(stream["avg_frame_rate"])
    format_record = probe.get("format", {})
    source = {
        "path": video_path.name,
        "sha256": sha256_file(video_path),
        "file_size_bytes": video_path.stat().st_size,
        "container_format": format_record.get("format_name"),
        "codec": stream.get("codec_name"),
        "width_pixels": width,
        "height_pixels": height,
        "pixel_format": stream.get("pix_fmt"),
        "frame_count": len(frame_records),
        "time_base": str(time_base),
        "start_pts_ticks": stream_start_ticks,
        "first_frame_pts_ticks": pts_ticks[0],
        "last_frame_pts_ticks": pts_ticks[-1],
        "stream_end_pts_ticks": stream_end_ticks,
        "stream_duration_seconds": rounded((stream_end_ticks - stream_start_ticks) * time_base),
        "nominal_frame_rate": str(nominal_rate),
        "nominal_frame_rate_hz": rounded(nominal_rate),
        "median_frame_duration_ticks": typical_duration_ticks,
        "median_frame_duration_seconds": rounded(typical_duration_ticks * time_base),
        "minimum_frame_duration_seconds": rounded(int(deltas.min()) * time_base)
        if len(deltas)
        else rounded(typical_duration_ticks * time_base),
        "maximum_frame_duration_seconds": rounded(int(deltas.max()) * time_base)
        if len(deltas)
        else rounded(typical_duration_ticks * time_base),
        "timestamp_basis": "ffprobe best_effort_timestamp in the source stream time base",
    }
    declared_frames = stream.get("nb_frames")
    if declared_frames is not None and int(declared_frames) != len(frame_records):
        raise RuntimeError(
            f"stream declares {declared_frames} frames but ffprobe returned {len(frame_records)}"
        )
    return source, frame_records, time_base


def read_exact(stream: BinaryIO, byte_count: int) -> bytes:
    chunks: list[bytes] = []
    remaining = byte_count
    while remaining:
        chunk = stream.read(remaining)
        if not chunk:
            break
        chunks.append(chunk)
        remaining -= len(chunk)
    return b"".join(chunks)


def decoded_frames(video_path: Path, width: int, height: int) -> Iterable[tuple[int, np.ndarray]]:
    command = [
        "ffmpeg",
        "-v",
        "error",
        "-i",
        str(video_path),
        "-map",
        "0:v:0",
        "-fps_mode",
        "passthrough",
        "-f",
        "rawvideo",
        "-pix_fmt",
        "rgb24",
        "pipe:1",
    ]
    try:
        process = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    except FileNotFoundError as error:
        raise RuntimeError("required executable not found: ffmpeg") from error
    assert process.stdout is not None
    frame_bytes = width * height * 3
    index = 0
    try:
        while True:
            raw = read_exact(process.stdout, frame_bytes)
            if not raw:
                break
            if len(raw) != frame_bytes:
                raise RuntimeError(
                    f"ffmpeg returned a partial frame ({len(raw)} of {frame_bytes} bytes)"
                )
            yield index, np.frombuffer(raw, dtype=np.uint8).reshape((height, width, 3))
            index += 1
    finally:
        process.stdout.close()
        stderr = process.stderr.read().decode("utf-8", "replace") if process.stderr else ""
        return_code = process.wait()
        if process.stderr:
            process.stderr.close()
        if return_code:
            raise RuntimeError(f"ffmpeg decode failed: {stderr.strip() or 'no diagnostic'}")


def color_excess_signal(frame: np.ndarray, spec: ColorSpec) -> float:
    x0, y0, x1, y1 = spec.roi
    roi = frame[y0:y1, x0:x1].astype(np.int16)
    target = roi[..., spec.channel_index]
    other_indices = [index for index in range(3) if index != spec.channel_index]
    excess = target - np.maximum(roi[..., other_indices[0]], roi[..., other_indices[1]])
    return float(np.percentile(excess, 99, method="linear"))


def extract_signals(
    video_path: Path,
    source: dict[str, Any],
) -> dict[str, np.ndarray]:
    values = {spec.name: [] for spec in COLOR_SPECS}
    decoded_count = 0
    for index, frame in decoded_frames(
        video_path, int(source["width_pixels"]), int(source["height_pixels"])
    ):
        if index >= int(source["frame_count"]):
            raise RuntimeError("ffmpeg decoded more frames than ffprobe reported")
        for spec in COLOR_SPECS:
            values[spec.name].append(color_excess_signal(frame, spec))
        decoded_count += 1
    if decoded_count != int(source["frame_count"]):
        raise RuntimeError(
            f"ffmpeg decoded {decoded_count} frames but ffprobe reported {source['frame_count']}"
        )
    return {name: np.asarray(signal, dtype=np.float64) for name, signal in values.items()}


def otsu_threshold(signal: np.ndarray) -> float:
    """Compute a deterministic one-signal-unit Otsu threshold."""

    low = math.floor(float(signal.min()))
    high = math.ceil(float(signal.max()))
    edges = np.arange(low, high + 2, dtype=np.float64)
    histogram, edges = np.histogram(signal, bins=edges)
    centers = (edges[:-1] + edges[1:]) / 2.0
    probabilities = histogram.astype(np.float64) / histogram.sum()
    weights = np.cumsum(probabilities)
    weighted_means = np.cumsum(probabilities * centers)
    total_mean = weighted_means[-1]
    between_class = np.full_like(weights, -1.0)
    valid = (weights > 0.0) & (weights < 1.0)
    between_class[valid] = (
        (total_mean * weights[valid] - weighted_means[valid]) ** 2
        / (weights[valid] * (1.0 - weights[valid]))
    )
    split_index = int(np.argmax(between_class[:-1]))
    return float(edges[split_index + 1])


def seconds_for_ticks(ticks: int | Fraction, time_base: Fraction) -> float:
    return rounded(ticks * time_base)


def transition_record(
    color: str,
    index: int,
    states: np.ndarray,
    signals: np.ndarray,
    frames: list[dict[str, int]],
    time_base: Fraction,
) -> dict[str, Any]:
    previous = frames[index - 1]
    current = frames[index]
    gap_ticks = current["pts_ticks"] - previous["pts_ticks"]
    midpoint_ticks = Fraction(previous["pts_ticks"] + current["pts_ticks"], 2)
    new_state = bool(states[index])
    return {
        "id": None,
        "color": color,
        "edge": "on" if new_state else "off",
        "previous_state": "on" if bool(states[index - 1]) else "off",
        "new_state": "on" if new_state else "off",
        "previous_source_frame": index - 1,
        "first_new_source_frame": index,
        "previous_frame_pts_ticks": previous["pts_ticks"],
        "first_new_frame_pts_ticks": current["pts_ticks"],
        "bracket_start_seconds": seconds_for_ticks(previous["pts_ticks"], time_base),
        "bracket_end_seconds": seconds_for_ticks(current["pts_ticks"], time_base),
        "estimated_timestamp_seconds": seconds_for_ticks(midpoint_ticks, time_base),
        "uncertainty_seconds": seconds_for_ticks(Fraction(gap_ticks, 2), time_base),
        "previous_signal": rounded(float(signals[index - 1]), 6),
        "new_signal": rounded(float(signals[index]), 6),
        "contact_sheet_cell": None,
    }


def on_run_bounds(states: np.ndarray) -> list[tuple[int, int]]:
    transitions = np.flatnonzero(states[1:] != states[:-1]) + 1
    starts = np.concatenate(([0], transitions))
    ends = np.concatenate((transitions, [len(states)]))
    return [(int(start), int(end)) for start, end in zip(starts, ends) if bool(states[start])]


def build_measurements(
    source: dict[str, Any],
    frames: list[dict[str, int]],
    time_base: Fraction,
    signals: dict[str, np.ndarray],
) -> tuple[dict[str, Any], dict[str, np.ndarray], list[dict[str, Any]], list[dict[str, Any]]]:
    frame_interval = float(source["median_frame_duration_seconds"])
    states_by_color: dict[str, np.ndarray] = {}
    colors: list[dict[str, Any]] = []
    all_transitions: list[dict[str, Any]] = []
    all_cycles: list[dict[str, Any]] = []

    for spec in COLOR_SPECS:
        signal = signals[spec.name]
        threshold = otsu_threshold(signal)
        states = signal >= threshold
        states_by_color[spec.name] = states
        transition_indices = (np.flatnonzero(states[1:] != states[:-1]) + 1).tolist()
        transitions = [
            transition_record(spec.name, index, states, signal, frames, time_base)
            for index in transition_indices
        ]
        all_transitions.extend(transitions)

        bounds = on_run_bounds(states)
        intervals: list[dict[str, Any]] = []
        for interval_index, (start, end) in enumerate(bounds, start=1):
            start_censored = start == 0
            end_censored = end == len(states)
            start_gap_ticks = (
                frames[start]["pts_ticks"] - frames[start - 1]["pts_ticks"]
                if not start_censored
                else 0
            )
            end_gap_ticks = (
                frames[end]["pts_ticks"] - frames[end - 1]["pts_ticks"]
                if not end_censored
                else 0
            )
            start_boundary_ticks: Fraction | int = (
                Fraction(frames[start - 1]["pts_ticks"] + frames[start]["pts_ticks"], 2)
                if not start_censored
                else frames[start]["pts_ticks"]
            )
            end_boundary_ticks: Fraction | int = (
                Fraction(frames[end - 1]["pts_ticks"] + frames[end]["pts_ticks"], 2)
                if not end_censored
                else int(source["stream_end_pts_ticks"])
            )
            duration = float((end_boundary_ticks - start_boundary_ticks) * time_base)
            visible_start_pts = frames[start]["pts_ticks"]
            visible_end_pts = (
                frames[end]["pts_ticks"] if end < len(frames) else int(source["stream_end_pts_ticks"])
            )
            interval = {
                "id": f"{spec.name[0].upper()}I{interval_index:03d}",
                "color": spec.name,
                "first_on_source_frame": start,
                "last_on_source_frame": end - 1,
                "visible_frame_count": end - start,
                "first_on_frame_pts_seconds": seconds_for_ticks(visible_start_pts, time_base),
                "end_exclusive_frame_pts_seconds": seconds_for_ticks(visible_end_pts, time_base),
                "start_censored": start_censored,
                "end_censored": end_censored,
                "censoring": (
                    "both"
                    if start_censored and end_censored
                    else "left"
                    if start_censored
                    else "right"
                    if end_censored
                    else "none"
                ),
                "estimated_start_transition_seconds": None
                if start_censored
                else seconds_for_ticks(start_boundary_ticks, time_base),
                "estimated_end_transition_seconds": None
                if end_censored
                else seconds_for_ticks(end_boundary_ticks, time_base),
                "on_duration_seconds": rounded(duration),
                "on_duration_kind": "lower_bound"
                if start_censored or end_censored
                else "bounded_estimate",
                "uncertainty_seconds": rounded(frame_interval),
                "period_seconds": None,
                "frequency_hz": None,
                "duty_cycle": None,
                "cycle_uncertainty_seconds": None,
                "mean_on_signal": rounded(float(signal[start:end].mean()), 6),
                "minimum_on_signal": rounded(float(signal[start:end].min()), 6),
                "start_transition_bracket_seconds": None
                if start_censored
                else [
                    seconds_for_ticks(frames[start - 1]["pts_ticks"], time_base),
                    seconds_for_ticks(frames[start]["pts_ticks"], time_base),
                ],
                "end_transition_bracket_seconds": None
                if end_censored
                else [
                    seconds_for_ticks(frames[end - 1]["pts_ticks"], time_base),
                    seconds_for_ticks(frames[end]["pts_ticks"], time_base),
                ],
                "boundary_uncertainty_seconds": rounded(
                    float(Fraction(start_gap_ticks + end_gap_ticks, 2) * time_base)
                )
                if not start_censored and not end_censored
                else None,
            }
            intervals.append(interval)

        cycles: list[dict[str, Any]] = []
        for interval_index, interval in enumerate(intervals[:-1]):
            next_interval = intervals[interval_index + 1]
            if interval["start_censored"] or next_interval["start_censored"]:
                continue
            if interval["end_censored"]:
                continue
            start_frame = int(interval["first_on_source_frame"])
            fall_frame = int(interval["last_on_source_frame"]) + 1
            next_start_frame = int(next_interval["first_on_source_frame"])
            start_midpoint = Fraction(
                frames[start_frame - 1]["pts_ticks"] + frames[start_frame]["pts_ticks"], 2
            )
            fall_midpoint = Fraction(
                frames[fall_frame - 1]["pts_ticks"] + frames[fall_frame]["pts_ticks"], 2
            )
            next_midpoint = Fraction(
                frames[next_start_frame - 1]["pts_ticks"]
                + frames[next_start_frame]["pts_ticks"],
                2,
            )
            on_duration = float((fall_midpoint - start_midpoint) * time_base)
            period = float((next_midpoint - start_midpoint) * time_base)
            start_uncertainty = Fraction(
                frames[start_frame]["pts_ticks"] - frames[start_frame - 1]["pts_ticks"], 2
            )
            next_uncertainty = Fraction(
                frames[next_start_frame]["pts_ticks"]
                - frames[next_start_frame - 1]["pts_ticks"],
                2,
            )
            period_uncertainty = float((start_uncertainty + next_uncertainty) * time_base)
            duty = on_duration / period
            cycle = {
                "id": f"{spec.name[0].upper()}C{len(cycles) + 1:03d}",
                "color": spec.name,
                "on_interval_id": interval["id"],
                "rise_source_frame": start_frame,
                "fall_first_off_source_frame": fall_frame,
                "next_rise_source_frame": next_start_frame,
                "on_duration_seconds": rounded(on_duration),
                "period_seconds": rounded(period),
                "frequency_hz": rounded(1.0 / period),
                "duty_cycle": rounded(duty),
                "uncertainty_seconds": rounded(period_uncertainty),
            }
            interval["period_seconds"] = cycle["period_seconds"]
            interval["frequency_hz"] = cycle["frequency_hz"]
            interval["duty_cycle"] = cycle["duty_cycle"]
            interval["cycle_uncertainty_seconds"] = cycle["uncertainty_seconds"]
            cycles.append(cycle)
            all_cycles.append(cycle)

        if not cycles:
            raise RuntimeError(f"{spec.name} has no complete cycles")
        periods = np.asarray([cycle["period_seconds"] for cycle in cycles], dtype=np.float64)
        on_durations = np.asarray(
            [cycle["on_duration_seconds"] for cycle in cycles], dtype=np.float64
        )
        aggregate_period = float(periods.mean())
        aggregate_duty = float(on_durations.sum() / periods.sum())
        onset_frames = [int(cycles[0]["rise_source_frame"])] + [
            int(cycle["next_rise_source_frame"]) for cycle in cycles
        ]
        onset_times = np.asarray(
            [
                float(
                    Fraction(
                        frames[frame_index - 1]["pts_ticks"]
                        + frames[frame_index]["pts_ticks"],
                        2,
                    )
                    * time_base
                )
                for frame_index in onset_frames
            ],
            dtype=np.float64,
        )
        edge_ordinals = np.arange(len(onset_times), dtype=np.float64)
        centered_ordinals = edge_ordinals - edge_ordinals.mean()
        regression_period = float(
            np.dot(centered_ordinals, onset_times - onset_times.mean())
            / np.dot(centered_ordinals, centered_ordinals)
        )
        measurement_uncertainty = frame_interval
        upright_roi = rotate_box_180(
            spec.roi, int(source["width_pixels"]), int(source["height_pixels"])
        )
        color_record = {
            "color": spec.name,
            "physical_region": (
                "phone-right lower lamp" if spec.name == "blue" else "phone-left lower lamp"
            ),
            "region": {
                "x0_inclusive": upright_roi[0],
                "y0_inclusive": upright_roi[1],
                "x1_exclusive": upright_roi[2],
                "y1_exclusive": upright_roi[3],
                "width_pixels": upright_roi[2] - upright_roi[0],
                "height_pixels": upright_roi[3] - upright_roi[1],
                "coordinate_origin": "top-left after rotating the source frame 180 degrees phone-upright",
            },
            "source_extraction_region": {
                "x0_inclusive": spec.roi[0],
                "y0_inclusive": spec.roi[1],
                "x1_exclusive": spec.roi[2],
                "y1_exclusive": spec.roi[3],
                "coordinate_origin": "top-left of the unrotated decoded source frame",
            },
            "color_channel": spec.channel,
            "signal_definition": f"99th percentile of {spec.channel} - max(other two RGB channels)",
            "threshold_method": "one-signal-unit Otsu threshold over all source frames",
            "threshold": rounded(threshold, 6),
            "off_signal_maximum": rounded(float(signal[~states].max()), 6),
            "on_signal_minimum": rounded(float(signal[states].min()), 6),
            "state_separation_margin": rounded(
                float(signal[states].min() - signal[~states].max()), 6
            ),
            "transition_count": len(transitions),
            "visible_on_interval_count": len(intervals),
            "complete_cycle_count": len(cycles),
            "period_seconds": rounded(aggregate_period),
            "period_edge_to_edge_mean_seconds": rounded(aggregate_period),
            "period_edge_regression_seconds": rounded(regression_period),
            "period_minimum_seconds": rounded(float(periods.min())),
            "period_maximum_seconds": rounded(float(periods.max())),
            "frequency_hz": rounded(1.0 / aggregate_period),
            "frequency_edge_regression_hz": rounded(1.0 / regression_period),
            "duty_cycle": rounded(aggregate_duty),
            "uncertainty_seconds": rounded(measurement_uncertainty),
            "frequency_uncertainty_hz": rounded(
                measurement_uncertainty / (aggregate_period * aggregate_period)
            ),
            "uncertainty_definition": (
                "one median source-frame interval for a duration or period; each transition "
                "timestamp is the midpoint of adjacent source PTS values with half-gap uncertainty"
            ),
            "aggregation": (
                "arithmetic mean of complete onset-to-onset periods; duty cycle is total complete "
                "on-time divided by total complete cycle time"
            ),
            "frequency_method": (
                "source-PTS onset edges using both edge-to-edge mean and ordinary least-squares "
                "edge-time regression; no FFT or FFT-bin estimate is used"
            ),
            "transitions": transitions,
            "visible_on_intervals": intervals,
            "complete_cycles": cycles,
        }
        colors.append(color_record)

    all_transitions.sort(
        key=lambda item: (item["first_new_source_frame"], COLOR_ORDER[item["color"]])
    )
    for contact_index, transition in enumerate(all_transitions):
        transition["id"] = f"T{contact_index + 1:04d}"
        transition["contact_sheet_cell"] = {
            "index_zero_based": contact_index,
            "row_one_based": contact_index // CONTACT_COLUMNS + 1,
            "column_one_based": contact_index % CONTACT_COLUMNS + 1,
        }

    # The dictionaries in each color record are the same objects as the sorted list.
    # Sort them back into temporal order for direct per-color reading.
    for color in colors:
        color["transitions"].sort(key=lambda item: item["first_new_source_frame"])

    transitions_by_color = {
        color["color"]: color["transitions"] for color in colors
    }
    blue_on_edges = [
        item for item in transitions_by_color["blue"] if item["edge"] == "on"
    ]
    blue_off_edges = [
        item for item in transitions_by_color["blue"] if item["edge"] == "off"
    ]
    red_on_edges = [
        item for item in transitions_by_color["red"] if item["edge"] == "on"
    ]
    green_on_edges = [
        item for item in transitions_by_color["green"] if item["edge"] == "on"
    ]
    six_state_cycles: list[dict[str, Any]] = []
    landmark_names = (
        "blue_on",
        "first_red_on",
        "first_green_on",
        "blue_off",
        "second_red_on",
        "second_green_on",
        "next_blue_on",
    )
    phase_names = ("blue", "blue+red", "blue+green", "off", "red", "green")
    for cycle_index, (blue_start, blue_next) in enumerate(
        zip(blue_on_edges, blue_on_edges[1:]), start=1
    ):
        start_frame = int(blue_start["first_new_source_frame"])
        next_frame = int(blue_next["first_new_source_frame"])
        red_edges = [
            item
            for item in red_on_edges
            if start_frame < int(item["first_new_source_frame"]) < next_frame
        ]
        green_edges = [
            item
            for item in green_on_edges
            if start_frame < int(item["first_new_source_frame"]) < next_frame
        ]
        blue_falls = [
            item
            for item in blue_off_edges
            if start_frame < int(item["first_new_source_frame"]) < next_frame
        ]
        if len(red_edges) != 2 or len(green_edges) != 2 or len(blue_falls) != 1:
            raise RuntimeError(
                "a complete blue cycle does not contain two red onsets, two green onsets, "
                "and one blue fall"
            )
        landmarks = [
            blue_start,
            red_edges[0],
            green_edges[0],
            blue_falls[0],
            red_edges[1],
            green_edges[1],
            blue_next,
        ]
        landmark_frames = [int(item["first_new_source_frame"]) for item in landmarks]
        if landmark_frames != sorted(landmark_frames) or len(set(landmark_frames)) != 7:
            raise RuntimeError("six-state phase landmarks are not strictly ordered")
        landmark_times = [float(item["estimated_timestamp_seconds"]) for item in landmarks]
        dwells = [right - left for left, right in zip(landmark_times, landmark_times[1:])]
        period = landmark_times[-1] - landmark_times[0]
        six_state_cycles.append(
            {
                "id": f"S{cycle_index:03d}",
                "phase_order": list(phase_names),
                "landmark_order": list(landmark_names),
                "landmark_transition_ids": [item["id"] for item in landmarks],
                "landmark_source_frames": landmark_frames,
                "landmark_timestamps_seconds": [rounded(value) for value in landmark_times],
                "phase_dwell_seconds": [rounded(value) for value in dwells],
                "cycle_period_seconds": rounded(period),
                "cycle_frequency_hz": rounded(1.0 / period),
                "mean_phase_dwell_seconds": rounded(period / 6.0),
                "uncertainty_seconds": rounded(frame_interval),
            }
        )

    if not six_state_cycles:
        raise RuntimeError("no complete six-state cycles were found")
    six_periods = np.asarray(
        [item["cycle_period_seconds"] for item in six_state_cycles], dtype=np.float64
    )
    six_dwells = np.asarray(
        [dwell for item in six_state_cycles for dwell in item["phase_dwell_seconds"]],
        dtype=np.float64,
    )
    blue_record = next(color for color in colors if color["color"] == "blue")
    six_state_summary = {
        "orientation": "phone upright after a 180-degree rotation of the source frames",
        "phase_order": list(phase_names),
        "phase_order_status": (
            "measured classification summarized at the six ordered activation landmarks; "
            "single-frame optical edge overlaps are preserved in frames.csv"
        ),
        "landmark_order": list(landmark_names),
        "complete_cycle_count": len(six_state_cycles),
        "cycle_period_seconds": rounded(float(six_periods.mean())),
        "cycle_period_edge_regression_seconds": blue_record[
            "period_edge_regression_seconds"
        ],
        "cycle_frequency_hz": rounded(1.0 / float(six_periods.mean())),
        "mean_state_dwell_seconds": rounded(float(six_dwells.mean())),
        "minimum_observed_state_dwell_seconds": rounded(float(six_dwells.min())),
        "maximum_observed_state_dwell_seconds": rounded(float(six_dwells.max())),
        "period_uncertainty_seconds": rounded(frame_interval),
        "individual_state_dwell_uncertainty_seconds": rounded(frame_interval),
        "timing_method": (
            "source-PTS activation-edge differences; the full-loop result is the blue "
            "onset-to-onset edge mean and no FFT estimate is used"
        ),
        "cycles": six_state_cycles,
    }

    red_states = states_by_color["red"]
    green_states = states_by_color["green"]
    simultaneous_right_frames = np.flatnonzero(red_states & green_states).tolist()
    measurements = {
        "schema_version": 1,
        "analyzer": {
            "path": "tools/analyze_led_service_video.py",
            "version": ANALYZER_VERSION,
        },
        "source": source,
        "method": {
            "timestamp_basis": source["timestamp_basis"],
            "transition_estimator": (
                "midpoint between the last source-frame PTS in the old state and the first "
                "source-frame PTS in the new state"
            ),
            "classification": "signal >= the per-color Otsu threshold is on",
            "decoded_pixel_format": "rgb24",
            "orientation": {
                "source_phone_orientation": "upside down",
                "reporting_orientation": "phone upright",
                "presentation_transform": "180-degree rotation",
                "coordinate_transform": (
                    "upright [x0,y0,x1,y1) = [width-x1,height-y1,width-x0,height-y0)"
                ),
                "signal_extraction_note": (
                    "signals are extracted from source_extraction_region before rotation; region "
                    "and every audit image use the equivalent phone-upright coordinates"
                ),
            },
            "raw_evidence": "frames.csv, transitions.csv, intervals.csv, and cycles.csv",
        },
        "colors": colors,
        "cross_color_measured_facts": {
            "simultaneous_red_and_green_frame_count": len(simultaneous_right_frames),
            "simultaneous_red_and_green_source_frames": simultaneous_right_frames,
        },
        "six_state_cycle": six_state_summary,
        "interpretation": [
            {
                "statement": (
                    "The phone-left lamp appears to alternate red and green pulses because no source "
                    "frame is classified as both colors and their on intervals interleave."
                ),
                "status": "timing interpretation, not a recovered electrical fact",
            },
            {
                "statement": (
                    "The phone-right blue pulse train appears synchronized at approximately twice "
                    "the period of either phone-left color."
                ),
                "status": "period interpretation, not a service-mode specification",
            },
        ],
        "evidence_files": {
            "per_source_frame_signals": "frames.csv",
            "transition_table": "transitions.csv",
            "visible_interval_table": "intervals.csv",
            "complete_cycle_table": "cycles.csv",
            "six_state_cycle_table": "six-state-cycles.csv",
            "roi_reference": "roi-reference.png",
            "transition_contact_sheet": "contact-sheet.png",
            "signal_plot": "signals.png",
            "human_report": "report.md",
        },
    }
    return measurements, states_by_color, all_transitions, all_cycles


def write_json(path: Path, value: Any) -> None:
    path.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8")


def write_csv(path: Path, header: list[str], rows: Iterable[list[Any]]) -> None:
    with path.open("w", encoding="utf-8", newline="") as output:
        writer = csv.writer(output, lineterminator="\n")
        writer.writerow(header)
        writer.writerows(rows)


def write_machine_evidence(
    output_dir: Path,
    measurements: dict[str, Any],
    frames: list[dict[str, int]],
    time_base: Fraction,
    signals: dict[str, np.ndarray],
    states: dict[str, np.ndarray],
    transitions: list[dict[str, Any]],
    cycles: list[dict[str, Any]],
) -> None:
    source = measurements["source"]
    write_json(output_dir / "source.json", source)
    write_json(output_dir / "measurements.json", measurements)

    frame_rows: list[list[Any]] = []
    for frame in frames:
        index = frame["index"]
        row: list[Any] = [
            index,
            frame["pts_ticks"],
            format_number(float(frame["pts_ticks"] * time_base), 9),
            frame["duration_ticks"],
            format_number(float(frame["duration_ticks"] * time_base), 9),
        ]
        for spec in COLOR_SPECS:
            row.extend(
                [
                    format_number(float(signals[spec.name][index]), 6),
                    int(states[spec.name][index]),
                ]
            )
        frame_rows.append(row)
    write_csv(
        output_dir / "frames.csv",
        [
            "source_frame_index",
            "source_pts_ticks",
            "source_timestamp_seconds",
            "source_duration_ticks",
            "source_duration_seconds",
            "blue_signal",
            "blue_on",
            "green_signal",
            "green_on",
            "red_signal",
            "red_on",
        ],
        frame_rows,
    )

    write_csv(
        output_dir / "transitions.csv",
        [
            "transition_id",
            "color",
            "edge",
            "previous_source_frame",
            "first_new_source_frame",
            "previous_frame_pts_ticks",
            "first_new_frame_pts_ticks",
            "bracket_start_seconds",
            "bracket_end_seconds",
            "estimated_timestamp_seconds",
            "uncertainty_seconds",
            "previous_signal",
            "new_signal",
            "contact_sheet_row",
            "contact_sheet_column",
        ],
        (
            [
                item["id"],
                item["color"],
                item["edge"],
                item["previous_source_frame"],
                item["first_new_source_frame"],
                item["previous_frame_pts_ticks"],
                item["first_new_frame_pts_ticks"],
                format_number(item["bracket_start_seconds"], 9),
                format_number(item["bracket_end_seconds"], 9),
                format_number(item["estimated_timestamp_seconds"], 9),
                format_number(item["uncertainty_seconds"], 9),
                format_number(item["previous_signal"], 6),
                format_number(item["new_signal"], 6),
                item["contact_sheet_cell"]["row_one_based"],
                item["contact_sheet_cell"]["column_one_based"],
            ]
            for item in transitions
        ),
    )

    intervals = [
        interval
        for color in measurements["colors"]
        for interval in color["visible_on_intervals"]
    ]
    write_csv(
        output_dir / "intervals.csv",
        [
            "interval_id",
            "color",
            "first_on_source_frame",
            "last_on_source_frame",
            "visible_frame_count",
            "first_on_frame_pts_seconds",
            "end_exclusive_frame_pts_seconds",
            "censoring",
            "estimated_start_transition_seconds",
            "estimated_end_transition_seconds",
            "on_duration_seconds",
            "on_duration_kind",
            "uncertainty_seconds",
            "period_seconds",
            "frequency_hz",
            "duty_cycle",
            "cycle_uncertainty_seconds",
        ],
        (
            [
                item["id"],
                item["color"],
                item["first_on_source_frame"],
                item["last_on_source_frame"],
                item["visible_frame_count"],
                format_number(item["first_on_frame_pts_seconds"], 9),
                format_number(item["end_exclusive_frame_pts_seconds"], 9),
                item["censoring"],
                format_number(item["estimated_start_transition_seconds"], 9),
                format_number(item["estimated_end_transition_seconds"], 9),
                format_number(item["on_duration_seconds"], 9),
                item["on_duration_kind"],
                format_number(item["uncertainty_seconds"], 9),
                format_number(item["period_seconds"], 9),
                format_number(item["frequency_hz"], 9),
                format_number(item["duty_cycle"], 9),
                format_number(item["cycle_uncertainty_seconds"], 9),
            ]
            for item in intervals
        ),
    )

    write_csv(
        output_dir / "cycles.csv",
        [
            "cycle_id",
            "color",
            "on_interval_id",
            "rise_source_frame",
            "fall_first_off_source_frame",
            "next_rise_source_frame",
            "on_duration_seconds",
            "period_seconds",
            "frequency_hz",
            "duty_cycle",
            "uncertainty_seconds",
        ],
        (
            [
                item["id"],
                item["color"],
                item["on_interval_id"],
                item["rise_source_frame"],
                item["fall_first_off_source_frame"],
                item["next_rise_source_frame"],
                format_number(item["on_duration_seconds"], 9),
                format_number(item["period_seconds"], 9),
                format_number(item["frequency_hz"], 9),
                format_number(item["duty_cycle"], 9),
                format_number(item["uncertainty_seconds"], 9),
            ]
            for item in cycles
        ),
    )

    write_csv(
        output_dir / "six-state-cycles.csv",
        [
            "cycle_id",
            "orientation",
            "phase_order",
            "landmark_order",
            "landmark_transition_ids",
            "landmark_source_frames",
            "landmark_timestamps_seconds",
            "phase_dwell_seconds",
            "cycle_period_seconds",
            "cycle_frequency_hz",
            "mean_phase_dwell_seconds",
            "uncertainty_seconds",
        ],
        (
            [
                item["id"],
                measurements["six_state_cycle"]["orientation"],
                ">".join(item["phase_order"]),
                ">".join(item["landmark_order"]),
                ">".join(item["landmark_transition_ids"]),
                ">".join(str(value) for value in item["landmark_source_frames"]),
                ">".join(format_number(value, 9) for value in item["landmark_timestamps_seconds"]),
                ">".join(format_number(value, 9) for value in item["phase_dwell_seconds"]),
                format_number(item["cycle_period_seconds"], 9),
                format_number(item["cycle_frequency_hz"], 9),
                format_number(item["mean_phase_dwell_seconds"], 9),
                format_number(item["uncertainty_seconds"], 9),
            ]
            for item in measurements["six_state_cycle"]["cycles"]
        ),
    )


def load_font(size: int, bold: bool = False) -> ImageFont.ImageFont:
    names = ["DejaVuSansMono-Bold.ttf", "DejaVuSansMono.ttf"] if bold else ["DejaVuSansMono.ttf"]
    for name in names:
        try:
            return ImageFont.truetype(name, size=size)
        except OSError:
            continue
    return ImageFont.load_default()


def draw_roi(
    image: Image.Image,
    roi: tuple[int, int, int, int],
    color: tuple[int, int, int],
    crop: tuple[int, int, int, int],
    scale: int,
) -> None:
    draw = ImageDraw.Draw(image)
    x0, y0, x1, y1 = roi
    cx0, cy0, _, _ = crop
    box = (
        (x0 - cx0) * scale,
        (y0 - cy0) * scale,
        (x1 - cx0) * scale - 1,
        (y1 - cy0) * scale - 1,
    )
    for inset in range(3):
        draw.rectangle(
            (box[0] + inset, box[1] + inset, box[2] - inset, box[3] - inset),
            outline=color,
        )


def make_transition_tile(
    previous: Image.Image,
    current: Image.Image,
    transition: dict[str, Any],
    font: ImageFont.ImageFont,
    small_font: ImageFont.ImageFont,
    source_width: int,
    source_height: int,
) -> Image.Image:
    spec = SPEC_BY_NAME[transition["color"]]
    scale = 3
    crop = rotate_box_180(spec.audit_crop, source_width, source_height)
    upright_roi = rotate_box_180(spec.roi, source_width, source_height)
    crop_width = crop[2] - crop[0]
    crop_height = crop[3] - crop[1]
    panel_width = crop_width * scale
    panel_height = crop_height * scale
    gap = 4
    tile_width = panel_width * 2 + gap + 12
    tile_height = panel_height + 52
    tile = Image.new("RGB", (tile_width, tile_height), "white")
    draw = ImageDraw.Draw(tile)
    draw.rectangle((0, 0, tile_width - 1, tile_height - 1), outline=(185, 185, 185))
    label = (
        f"{transition['id']} {transition['color'].upper()} ->{transition['edge'].upper()} "
        f"t={transition['estimated_timestamp_seconds']:.6f}s"
    )
    detail = (
        f"f{transition['previous_source_frame']} {transition['bracket_start_seconds']:.6f}  |  "
        f"f{transition['first_new_source_frame']} {transition['bracket_end_seconds']:.6f}"
    )
    draw.text((6, 4), label, fill=spec.display_rgb, font=font)
    draw.text((6, 24), detail, fill=(30, 30, 30), font=small_font)
    y = 46
    previous_upright = previous.transpose(Image.Transpose.ROTATE_180)
    current_upright = current.transpose(Image.Transpose.ROTATE_180)
    left = previous_upright.crop(crop).resize(
        (panel_width, panel_height), Image.Resampling.NEAREST
    )
    right = current_upright.crop(crop).resize(
        (panel_width, panel_height), Image.Resampling.NEAREST
    )
    draw_roi(left, upright_roi, spec.display_rgb, crop, scale)
    draw_roi(right, upright_roi, spec.display_rgb, crop, scale)
    tile.paste(left, (5, y))
    tile.paste(right, (5 + panel_width + gap, y))
    return tile


def make_visual_evidence(
    video_path: Path,
    output_dir: Path,
    source: dict[str, Any],
    signals: dict[str, np.ndarray],
    states: dict[str, np.ndarray],
    transitions: list[dict[str, Any]],
) -> None:
    font = load_font(13, bold=True)
    small_font = load_font(11)
    source_width = int(source["width_pixels"])
    source_height = int(source["height_pixels"])
    transitions_by_frame: dict[int, list[dict[str, Any]]] = {}
    for transition in transitions:
        transitions_by_frame.setdefault(transition["first_new_source_frame"], []).append(transition)
    peak_frames = {
        spec.name: int(np.argmax(signals[spec.name])) for spec in COLOR_SPECS
    }
    reference_frames: dict[int, Image.Image] = {}
    tiles: list[Image.Image | None] = [None] * len(transitions)
    previous: Image.Image | None = None
    decoded_count = 0
    for index, frame in decoded_frames(
        video_path, int(source["width_pixels"]), int(source["height_pixels"])
    ):
        current = Image.fromarray(frame.copy(), mode="RGB")
        if index in peak_frames.values():
            reference_frames[index] = current.copy()
        if index in transitions_by_frame:
            if previous is None:
                raise RuntimeError("an internal transition unexpectedly occurs at source frame zero")
            for transition in transitions_by_frame[index]:
                contact_index = transition["contact_sheet_cell"]["index_zero_based"]
                tiles[contact_index] = make_transition_tile(
                    previous,
                    current,
                    transition,
                    font,
                    small_font,
                    source_width,
                    source_height,
                )
        previous = current
        decoded_count += 1
    if decoded_count != int(source["frame_count"]):
        raise RuntimeError("visual-evidence decode did not cover every source frame")
    if any(tile is None for tile in tiles):
        missing = [index for index, tile in enumerate(tiles) if tile is None]
        raise RuntimeError(f"contact sheet is missing transition tiles: {missing}")

    typed_tiles = [tile for tile in tiles if tile is not None]
    tile_width, tile_height = typed_tiles[0].size
    header_height = 66
    rows = math.ceil(len(typed_tiles) / CONTACT_COLUMNS)
    sheet = Image.new(
        "RGB", (tile_width * CONTACT_COLUMNS, header_height + tile_height * rows), "white"
    )
    sheet_draw = ImageDraw.Draw(sheet)
    sheet_draw.text(
        (8, 6),
        "Every classified LED transition: adjacent source frames with the measured ROI boxed",
        fill=(0, 0, 0),
        font=load_font(17, bold=True),
    )
    sheet_draw.text(
        (8, 33),
        (
            f"{len(transitions)} transitions; timestamp is bracket midpoint; each tile gives "
            "last-old and first-new source-frame PTS; all phone crops are rotated 180 degrees upright"
        ),
        fill=(30, 30, 30),
        font=small_font,
    )
    for index, tile in enumerate(typed_tiles):
        x = (index % CONTACT_COLUMNS) * tile_width
        y = header_height + (index // CONTACT_COLUMNS) * tile_height
        sheet.paste(tile, (x, y))
    sheet.save(output_dir / "contact-sheet.png", format="PNG", compress_level=9)

    reference_scale = 3
    upright_phone_crop = rotate_box_180(PHONE_CROP, source_width, source_height)
    crop_width = upright_phone_crop[2] - upright_phone_crop[0]
    crop_height = upright_phone_crop[3] - upright_phone_crop[1]
    panel_width = crop_width * reference_scale
    panel_height = crop_height * reference_scale
    ref_header = 68
    reference = Image.new(
        "RGB", (panel_width * len(COLOR_SPECS), ref_header + panel_height), "white"
    )
    ref_draw = ImageDraw.Draw(reference)
    ref_draw.text(
        (8, 6),
        "Fixed LED measurement regions after 180-degree rotation to phone-upright orientation",
        fill=(0, 0, 0),
        font=load_font(17, bold=True),
    )
    for column, spec in enumerate(COLOR_SPECS):
        frame_index = peak_frames[spec.name]
        upright_frame = reference_frames[frame_index].transpose(Image.Transpose.ROTATE_180)
        panel = upright_frame.crop(upright_phone_crop).resize(
            (panel_width, panel_height), Image.Resampling.NEAREST
        )
        upright_roi = rotate_box_180(spec.roi, source_width, source_height)
        draw_roi(panel, upright_roi, spec.display_rgb, upright_phone_crop, reference_scale)
        x = column * panel_width
        reference.paste(panel, (x, ref_header))
        ref_draw.text(
            (x + 6, 34),
            (
                f"{spec.name.upper()} {('PHONE RIGHT' if spec.name == 'blue' else 'PHONE LEFT')} "
                f"{spec.channel}-excess ROI {upright_roi}; peak source f{frame_index}"
            ),
            fill=spec.display_rgb,
            font=small_font,
        )
    reference.save(output_dir / "roi-reference.png", format="PNG", compress_level=9)


def make_signal_plot(
    output_path: Path,
    source: dict[str, Any],
    frames: list[dict[str, int]],
    time_base: Fraction,
    measurements: dict[str, Any],
    signals: dict[str, np.ndarray],
    states: dict[str, np.ndarray],
) -> None:
    width, height = 1800, 1050
    image = Image.new("RGB", (width, height), "white")
    draw = ImageDraw.Draw(image)
    title_font = load_font(20, bold=True)
    font = load_font(13)
    small_font = load_font(11)
    draw.text(
        (84, 12),
        "LED color-excess signals, Otsu thresholds, and every classified transition",
        fill=(0, 0, 0),
        font=title_font,
    )
    plot_left, plot_right = 100, width - 35
    panel_top = 70
    panel_height = 275
    panel_gap = 42
    stream_start = float(int(source["start_pts_ticks"]) * time_base)
    stream_end = float(int(source["stream_end_pts_ticks"]) * time_base)
    times = np.asarray([float(frame["pts_ticks"] * time_base) for frame in frames])
    color_records = {item["color"]: item for item in measurements["colors"]}

    def x_for_time(value: float) -> int:
        return int(
            round(
                plot_left
                + (value - stream_start)
                / (stream_end - stream_start)
                * (plot_right - plot_left)
            )
        )

    for panel_index, spec in enumerate(COLOR_SPECS):
        top = panel_top + panel_index * (panel_height + panel_gap)
        bottom = top + panel_height
        signal = signals[spec.name]
        threshold = float(color_records[spec.name]["threshold"])
        signal_min = min(float(signal.min()), threshold) - 8.0
        signal_max = max(float(signal.max()), threshold) + 8.0

        def y_for_signal(value: float) -> int:
            return int(
                round(
                    bottom
                    - (value - signal_min) / (signal_max - signal_min) * panel_height
                )
            )

        for start, end in on_run_bounds(states[spec.name]):
            start_time = float(frames[start]["pts_ticks"] * time_base)
            end_time = (
                float(frames[end]["pts_ticks"] * time_base)
                if end < len(frames)
                else stream_end
            )
            draw.rectangle(
                (x_for_time(start_time), top, x_for_time(end_time), bottom),
                fill=spec.pale_rgb,
            )
        draw.rectangle((plot_left, top, plot_right, bottom), outline=(90, 90, 90), width=1)
        threshold_y = y_for_signal(threshold)
        for x in range(plot_left, plot_right, 10):
            draw.line((x, threshold_y, min(x + 5, plot_right), threshold_y), fill=(70, 70, 70))
        points = [(x_for_time(float(t)), y_for_signal(float(v))) for t, v in zip(times, signal)]
        draw.line(points, fill=spec.display_rgb, width=2)
        for transition in color_records[spec.name]["transitions"]:
            x = x_for_time(float(transition["estimated_timestamp_seconds"]))
            marker_color = spec.display_rgb if transition["edge"] == "on" else (70, 70, 70)
            draw.line((x, top, x, bottom), fill=marker_color, width=1)
        draw.text(
            (6, top + 4),
            spec.name.upper(),
            fill=spec.display_rgb,
            font=font,
        )
        draw.text(
            (6, top + 25),
            f"{spec.channel}-excess",
            fill=(20, 20, 20),
            font=small_font,
        )
        draw.text(
            (plot_left + 6, top + 5),
            (
                f"threshold={threshold:.3f}; {color_records[spec.name]['transition_count']} "
                f"transitions; pale bands=classified on"
            ),
            fill=(20, 20, 20),
            font=small_font,
        )
        draw.text(
            (plot_right - 128, top + 5),
            f"max {signal.max():.1f}",
            fill=(20, 20, 20),
            font=small_font,
        )
        draw.text(
            (plot_right - 128, bottom - 18),
            f"min {signal.min():.1f}",
            fill=(20, 20, 20),
            font=small_font,
        )
        tick = math.ceil(stream_start / 2.0) * 2
        while tick <= stream_end:
            x = x_for_time(float(tick))
            draw.line((x, bottom, x, bottom + 5), fill=(0, 0, 0))
            draw.text((x - 12, bottom + 7), f"{tick:g}", fill=(0, 0, 0), font=small_font)
            tick += 2
        draw.text(
            (plot_right - 58, bottom + 7), "s (PTS)", fill=(0, 0, 0), font=small_font
        )
    draw.text(
        (100, height - 28),
        (
            "Vertical markers are transition midpoint estimates; exact adjacent-frame PTS "
            "brackets and signals are in transitions.csv."
        ),
        fill=(30, 30, 30),
        font=font,
    )
    image.save(output_path, format="PNG", compress_level=9)


def transition_lines(color: dict[str, Any]) -> list[str]:
    entries = [
        f"{item['id']} {item['estimated_timestamp_seconds']:.6f}s {item['edge']}"
        for item in color["transitions"]
    ]
    return [", ".join(entries[index : index + 8]) for index in range(0, len(entries), 8)]


def write_report(output_path: Path, measurements: dict[str, Any]) -> None:
    source = measurements["source"]
    lines = [
        "# Physical-phone LED video measurements",
        "",
        "## Measured facts",
        "",
        (
            f"Source `{source['path']}` has SHA-256 `{source['sha256']}`, "
            f"{source['frame_count']} source frames at nominal {source['nominal_frame_rate']} fps, "
            f"a {source['width_pixels']}x{source['height_pixels']} frame, time base "
            f"{source['time_base']}, and stream duration {source['stream_duration_seconds']:.6f} s. "
            "All times below come from each decoded frame's source PTS."
        ),
        "",
        (
            "The phone is upside down in the source. Every region and audit image below is "
            "rotated 180 degrees to phone-upright orientation. Thus, blue is on the phone-right "
            "lamp, while red and green share the phone-left lamp. `measurements.json` also keeps "
            "the exact unrotated source extraction boxes for reproduction."
        ),
        "",
        (
            "A transition time is the midpoint of the last-old and first-new source-frame PTS. "
            f"Each transition has half-frame uncertainty (normally +/-"
            f"{source['median_frame_duration_seconds'] / 2:.6f} s). A duration or period has "
            f"one-frame uncertainty (+/-{source['median_frame_duration_seconds']:.6f} s)."
        ),
        "",
        "| Color | Phone-upright region `[x0,y0,x1,y1)` | Channel signal | Threshold | Visible on intervals | Complete cycles | Edge-mean period (s) | OLS-edge period (s) | Edge-mean frequency (Hz) | OLS-edge frequency (Hz) | Duty cycle | Period uncertainty (s) |",
        "| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |",
    ]
    for color in measurements["colors"]:
        region = color["region"]
        roi = (
            f"[{region['x0_inclusive']},{region['y0_inclusive']},"
            f"{region['x1_exclusive']},{region['y1_exclusive']})"
        )
        lines.append(
            "| "
            + " | ".join(
                [
                    color["color"],
                    f"{color['physical_region']} {roi}",
                    color["signal_definition"],
                    f"{color['threshold']:.3f}",
                    str(color["visible_on_interval_count"]),
                    str(color["complete_cycle_count"]),
                    f"{color['period_edge_to_edge_mean_seconds']:.6f}",
                    f"{color['period_edge_regression_seconds']:.6f}",
                    f"{color['frequency_hz']:.6f}",
                    f"{color['frequency_edge_regression_hz']:.6f}",
                    f"{color['duty_cycle']:.6f}",
                    f"{color['uncertainty_seconds']:.6f}",
                ]
            )
            + " |"
        )
    lines.extend(
        [
            "",
            (
                "Periods are arithmetic means of complete onset-to-onset cycles. Duty cycle is "
                "total measured on-time divided by total time in those cycles. Boundary-clipped "
                "intervals remain in `intervals.csv`, but they are excluded from aggregates. The "
                "OLS values regress source-PTS onset time against edge ordinal. No FFT or FFT-bin "
                "frequency is used."
            ),
            "",
            "## Six-state cycle",
            "",
            (
                "The repeated phone-upright classified phase order is `blue -> blue+red -> "
                "blue+green -> off -> red -> green`. Its ordered activation landmarks are "
                "`blue_on -> first_red_on -> first_green_on -> blue_off -> second_red_on -> "
                "second_green_on -> next_blue_on`."
            ),
            "",
            (
                f"The analysis contains "
                f"{measurements['six_state_cycle']['complete_cycle_count']} complete loops. "
                f"The edge-mean loop period is "
                f"{measurements['six_state_cycle']['cycle_period_seconds']:.6f} s. "
                f"The loop frequency is "
                f"{measurements['six_state_cycle']['cycle_frequency_hz']:.6f} Hz. "
                f"The mean state dwell is "
                f"{measurements['six_state_cycle']['mean_state_dwell_seconds']:.6f} s. "
                f"Individual measured dwells range from "
                f"{measurements['six_state_cycle']['minimum_observed_state_dwell_seconds']:.6f} "
                f"to {measurements['six_state_cycle']['maximum_observed_state_dwell_seconds']:.6f} "
                f"s with one-frame (+/-"
                f"{measurements['six_state_cycle']['individual_state_dwell_uncertainty_seconds']:.6f} "
                "s) resolution. The phase order summarizes the measured classification at six "
                "activation landmarks. `frames.csv` preserves single-frame optical edge overlaps."
            ),
            "",
            "## Transition times",
            "",
            (
                "This is the complete transition ledger. Each entry gives contact-sheet ID, "
                "estimated source-PTS boundary time, and new state. Exact PTS brackets, local "
                "uncertainties, signals, source frames, and contact-sheet cells are in "
                "`transitions.csv` and `measurements.json`."
            ),
            "",
        ]
    )
    for color in measurements["colors"]:
        lines.append(
            f"### {color['color'].capitalize()} ({color['transition_count']} transitions)"
        )
        lines.append("")
        lines.extend(transition_lines(color))
        lines.append("")
    lines.extend(
        [
            "## Audit evidence",
            "",
            "- `roi-reference.png` shows each fixed ROI on a peak source frame.",
            "- `signals.png` plots every per-frame signal, threshold, on span, and transition.",
            "- `contact-sheet.png` contains a labeled adjacent-frame pair for every transition.",
            "- `frames.csv` preserves every source PTS, frame duration, signal, and classified state.",
            "- `intervals.csv` lists every visible on interval, including clipped intervals.",
            "- `cycles.csv` preserves each complete period, frequency, duty cycle, and uncertainty.",
            "- `six-state-cycles.csv` preserves all six landmarks and six dwell times for every complete loop.",
            "",
            "Reproduce and verify the bundle from the repository root:",
            "",
            "```sh",
            (
                "python3 tools/analyze_led_service_video.py --verify "
                "/path/to/input.mp4 /path/to/output-directory"
            ),
            "```",
            "",
            "## Interpretation (not directly measured)",
            "",
        ]
    )
    for item in measurements["interpretation"]:
        lines.append(f"- {item['statement']} ({item['status']})")
    lines.append("")
    output_path.write_text("\n".join(lines), encoding="utf-8")


def write_manifest(output_dir: Path, source_sha256: str) -> None:
    files = {
        name: {
            "sha256": sha256_file(output_dir / name),
            "size_bytes": (output_dir / name).stat().st_size,
        }
        for name in OUTPUT_FILES
    }
    write_json(
        output_dir / "manifest.json",
        {
            "schema_version": 1,
            "analyzer_version": ANALYZER_VERSION,
            "source_sha256": source_sha256,
            "files": files,
        },
    )


def analyze(video_path: Path, output_dir: Path) -> dict[str, Any]:
    if not video_path.is_file():
        raise RuntimeError(f"input video does not exist: {video_path}")
    output_dir.mkdir(parents=True, exist_ok=True)
    source, frames, time_base = probe_source(video_path)
    signals = extract_signals(video_path, source)
    measurements, states, transitions, cycles = build_measurements(
        source, frames, time_base, signals
    )
    write_machine_evidence(
        output_dir, measurements, frames, time_base, signals, states, transitions, cycles
    )
    make_visual_evidence(video_path, output_dir, source, signals, states, transitions)
    make_signal_plot(
        output_dir / "signals.png",
        source,
        frames,
        time_base,
        measurements,
        signals,
        states,
    )
    write_report(output_dir / "report.md", measurements)
    write_manifest(output_dir, source["sha256"])
    return measurements


def csv_row_count(path: Path) -> int:
    with path.open("r", encoding="utf-8", newline="") as source:
        return sum(1 for _ in csv.reader(source)) - 1


def verify_bundle(output_dir: Path) -> tuple[int, int, int]:
    measurements = json.loads((output_dir / "measurements.json").read_text(encoding="utf-8"))
    source = measurements["source"]
    orientation = measurements["method"].get("orientation", {})
    if orientation.get("source_phone_orientation") != "upside down":
        raise RuntimeError("source orientation is not recorded as upside down")
    if orientation.get("reporting_orientation") != "phone upright":
        raise RuntimeError("reporting orientation is not phone upright")
    frame_count = int(source["frame_count"])
    colors = measurements["colors"]
    if {item["color"] for item in colors} != {"blue", "green", "red"}:
        raise RuntimeError("measurements.json does not contain exactly blue, green, and red")
    if csv_row_count(output_dir / "frames.csv") != frame_count:
        raise RuntimeError("frames.csv does not contain exactly one row per source frame")

    transition_ids: list[str] = []
    interval_count = 0
    cycle_count = 0
    for color in colors:
        for required in (
            "region",
            "color_channel",
            "transitions",
            "period_seconds",
            "frequency_hz",
            "duty_cycle",
            "uncertainty_seconds",
        ):
            if required not in color:
                raise RuntimeError(f"{color['color']} is missing {required}")
        if not (color["period_seconds"] > 0 and color["frequency_hz"] > 0):
            raise RuntimeError(f"{color['color']} has invalid period or frequency")
        if not 0 < color["duty_cycle"] < 1:
            raise RuntimeError(f"{color['color']} has invalid duty cycle")
        if color["uncertainty_seconds"] <= 0:
            raise RuntimeError(f"{color['color']} has invalid uncertainty")
        region = color["region"]
        if not (
            0 <= region["x0_inclusive"] < region["x1_exclusive"] <= source["width_pixels"]
            and 0
            <= region["y0_inclusive"]
            < region["y1_exclusive"]
            <= source["height_pixels"]
        ):
            raise RuntimeError(f"{color['color']} ROI is outside the source frame")
        if color["state_separation_margin"] <= 0:
            raise RuntimeError(f"{color['color']} on/off signal bands overlap")
        if color["complete_cycle_count"] != len(color["complete_cycles"]):
            raise RuntimeError(f"{color['color']} cycle count mismatch")
        if color["visible_on_interval_count"] != len(color["visible_on_intervals"]):
            raise RuntimeError(f"{color['color']} interval count mismatch")
        if color["transition_count"] != len(color["transitions"]):
            raise RuntimeError(f"{color['color']} transition count mismatch")
        for transition in color["transitions"]:
            if transition["first_new_source_frame"] != transition["previous_source_frame"] + 1:
                raise RuntimeError(f"{transition['id']} does not bracket adjacent source frames")
            if transition["bracket_start_seconds"] >= transition["bracket_end_seconds"]:
                raise RuntimeError(f"{transition['id']} has an invalid timestamp bracket")
            midpoint = (transition["bracket_start_seconds"] + transition["bracket_end_seconds"]) / 2
            if abs(midpoint - transition["estimated_timestamp_seconds"]) > 1e-8:
                raise RuntimeError(f"{transition['id']} midpoint is inconsistent")
            transition_ids.append(transition["id"])
        for interval in color["visible_on_intervals"]:
            if interval["visible_frame_count"] != (
                interval["last_on_source_frame"] - interval["first_on_source_frame"] + 1
            ):
                raise RuntimeError(f"{interval['id']} has an inconsistent frame span")
            for required in (
                "period_seconds",
                "frequency_hz",
                "duty_cycle",
                "uncertainty_seconds",
            ):
                if required not in interval:
                    raise RuntimeError(f"{interval['id']} is missing {required}")
        for cycle in color["complete_cycles"]:
            if abs(cycle["frequency_hz"] * cycle["period_seconds"] - 1.0) > 1e-6:
                raise RuntimeError(f"{cycle['id']} has an inconsistent frequency")
            if abs(
                cycle["duty_cycle"]
                - cycle["on_duration_seconds"] / cycle["period_seconds"]
            ) > 1e-6:
                raise RuntimeError(f"{cycle['id']} has an inconsistent duty cycle")
        interval_count += len(color["visible_on_intervals"])
        cycle_count += len(color["complete_cycles"])

    sorted_ids = sorted(transition_ids, key=lambda value: int(value[1:]))
    expected_ids = [f"T{index + 1:04d}" for index in range(len(transition_ids))]
    if sorted_ids != expected_ids:
        raise RuntimeError("transition IDs are not complete and contiguous")
    if csv_row_count(output_dir / "transitions.csv") != len(transition_ids):
        raise RuntimeError("transitions.csv row count mismatch")
    if csv_row_count(output_dir / "intervals.csv") != interval_count:
        raise RuntimeError("intervals.csv row count mismatch")
    if csv_row_count(output_dir / "cycles.csv") != cycle_count:
        raise RuntimeError("cycles.csv row count mismatch")
    if measurements["cross_color_measured_facts"]["simultaneous_red_and_green_frame_count"]:
        raise RuntimeError("red and green classifications overlap in the shared ROI")
    by_name = {color["color"]: color for color in colors}
    if by_name["blue"]["physical_region"] != "phone-right lower lamp":
        raise RuntimeError("blue is not labeled on the phone-right lamp")
    if any(
        by_name[name]["physical_region"] != "phone-left lower lamp"
        for name in ("red", "green")
    ):
        raise RuntimeError("red/green are not labeled on the phone-left lamp")
    six_state = measurements.get("six_state_cycle", {})
    expected_phase_order = ["blue", "blue+red", "blue+green", "off", "red", "green"]
    if six_state.get("phase_order") != expected_phase_order:
        raise RuntimeError("six-state phase order is missing or incorrect")
    if six_state.get("orientation") != "phone upright after a 180-degree rotation of the source frames":
        raise RuntimeError("six-state cycle orientation is not phone upright")
    if six_state.get("complete_cycle_count") != len(six_state.get("cycles", [])):
        raise RuntimeError("six-state complete-cycle count mismatch")
    if csv_row_count(output_dir / "six-state-cycles.csv") != six_state["complete_cycle_count"]:
        raise RuntimeError("six-state-cycles.csv row count mismatch")
    if not (
        six_state["cycle_period_seconds"] > 0
        and six_state["cycle_frequency_hz"] > 0
        and six_state["mean_state_dwell_seconds"] > 0
    ):
        raise RuntimeError("six-state timing is invalid")
    if abs(
        six_state["cycle_frequency_hz"] * six_state["cycle_period_seconds"] - 1.0
    ) > 1e-6:
        raise RuntimeError("six-state period and frequency are inconsistent")
    if abs(
        six_state["mean_state_dwell_seconds"] * 6.0
        - six_state["cycle_period_seconds"]
    ) > 1e-6:
        raise RuntimeError("six-state mean dwell is inconsistent with its loop period")

    manifest = json.loads((output_dir / "manifest.json").read_text(encoding="utf-8"))
    if set(manifest["files"]) != set(OUTPUT_FILES):
        raise RuntimeError("manifest file set is incomplete")
    for name, record in manifest["files"].items():
        path = output_dir / name
        if not path.is_file() or path.stat().st_size <= 0:
            raise RuntimeError(f"missing or empty evidence file: {name}")
        if sha256_file(path) != record["sha256"] or path.stat().st_size != record["size_bytes"]:
            raise RuntimeError(f"manifest mismatch for {name}")
    for name in ("roi-reference.png", "signals.png", "contact-sheet.png"):
        with Image.open(output_dir / name) as image:
            image.verify()
    report = (output_dir / "report.md").read_text(encoding="utf-8")
    if not all(transition_id in report for transition_id in transition_ids):
        raise RuntimeError("human report does not list every transition ID")
    return len(transition_ids), interval_count, cycle_count


def compare_bundles(first: Path, second: Path) -> None:
    expected = set(OUTPUT_FILES) | {"manifest.json"}
    first_files = {path.name for path in first.iterdir() if path.is_file()}
    second_files = {path.name for path in second.iterdir() if path.is_file()}
    if not expected.issubset(first_files) or not expected.issubset(second_files):
        raise RuntimeError("one deterministic replay bundle is incomplete")
    for name in sorted(expected):
        if sha256_file(first / name) != sha256_file(second / name):
            raise RuntimeError(f"deterministic replay differs for {name}")


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--verify",
        action="store_true",
        help="validate the bundle and compare it with a second deterministic replay",
    )
    parser.add_argument("video", type=Path, help="source MP4/MOV video")
    parser.add_argument("output_dir", type=Path, help="analysis output directory")
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(sys.argv[1:] if argv is None else argv)
    if shutil.which("ffmpeg") is None or shutil.which("ffprobe") is None:
        print("ERROR: ffmpeg and ffprobe are required", file=sys.stderr)
        return 2
    try:
        measurements = analyze(args.video, args.output_dir)
        if args.verify:
            transition_count, interval_count, cycle_count = verify_bundle(args.output_dir)
            with tempfile.TemporaryDirectory(prefix="led-video-analysis-verify-") as temp_name:
                replay_dir = Path(temp_name) / "analysis"
                analyze(args.video, replay_dir)
                verify_bundle(replay_dir)
                compare_bundles(args.output_dir, replay_dir)
            print(
                "VERIFY PASS: "
                f"{measurements['source']['frame_count']} source frames, "
                f"{transition_count} transitions, {interval_count} visible on intervals, "
                f"{cycle_count} complete cycles; colors=blue,green,red; "
                "deterministic replay matched"
            )
        else:
            summary = ", ".join(
                f"{item['color']}={item['frequency_hz']:.6f} Hz"
                for item in measurements["colors"]
            )
            print(f"Analysis written to {args.output_dir}: {summary}")
        return 0
    except (RuntimeError, ValueError, KeyError, OSError, json.JSONDecodeError) as error:
        print(f"ERROR: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
