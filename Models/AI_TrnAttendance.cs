using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;


namespace Agent.Models
{
    [Table("AI_vwGet_TrnAttendance", Schema = "dbo")]
    public class AI_TrnAttendance
    {
        // ── Identity ─────────────────────────────────────────────────────────────
        [Key]
        public int EmployeeID { get; set; }
        public string? Lastname { get; set; }
        public string? Firstname { get; set; }
        public string? Middlename { get; set; }

        // ── Date & Time ───────────────────────────────────────────────────────────
        // TranDate, TimeIN/OUT, BreakOUT/IN are all DateTime in SQL
        // ShiftTimeIn/Out store time as DateTime (1899-12-30 HH:mm:ss pattern)
        public DateTime? TranDate { get; set; }
        public string? DayOfTheWeek { get; set; }
        public DateTime? TimeIN { get; set; }
        public DateTime? TimeOUT { get; set; }
        public DateTime? BreakOUT { get; set; }
        public DateTime? BreakIN { get; set; }
        public DateTime? ShiftTimeIn { get; set; }
        public DateTime? ShiftTimeOut { get; set; }
        public string? ShiftTimeDesc { get; set; }

        // ── Metrics ───────────────────────────────────────────────────────────────
        public decimal? TotalHr { get; set; }
        public decimal? Tardy { get; set; }
        public decimal? Absence { get; set; }
        public decimal? Undertime { get; set; }

        // ── Metadata ──────────────────────────────────────────────────────────────
        public string? Remarks { get; set; }
        public string? LeaveTypeDesc { get; set; }
        public bool? WithCOA { get; set; }
        public string? ClientName { get; set; }

        // ── OT / Holiday Breakdown ────────────────────────────────────────────────
        public decimal? RegularOT { get; set; }
        public decimal? RegularDayNight { get; set; }
        public decimal? RegularOTNight { get; set; }
        public decimal? RestDay { get; set; }
        public decimal? RestDayOT { get; set; }
        public decimal? RestDayNight { get; set; }
        public decimal? RestDayOTNight { get; set; }
        public decimal? LegalHoliday { get; set; }
        public decimal? LegalHolidayOT { get; set; }
        public decimal? LegalHolidayNight { get; set; }
        public decimal? LegalHolidayOTNight { get; set; }
        public decimal? RestDayLegalHoliday { get; set; }
        public decimal? RestDayLegalOT { get; set; }
        public decimal? RestDayLegalNight { get; set; }
        public decimal? RestDayLegalOTNight { get; set; }
        public decimal? SpecialHoliday { get; set; }
        public decimal? SpecialHolidayOT { get; set; }
        public decimal? SpecialHolidayNight { get; set; }
        public decimal? SpecialHolidayOTNight { get; set; }
        public decimal? RestDaySpecialHoliday { get; set; }
        public decimal? RestDaySpecialOT { get; set; }
        public decimal? RestDaySpecialNight { get; set; }
        public decimal? RestDaySpecialOTNight { get; set; }

       

        /// <summary>
        /// Aggregates all OT/holiday hours into a single readable breakdown.
        /// Returns null if all values are zero — keeps JSON lean.
        /// </summary>
        [NotMapped]
        public object? OvertimeBreakdown
        {
            get
            {
                var entries = new Dictionary<string, decimal>
                {
                    ["Regular OT"] = RegularOT ?? 0,
                    ["Regular Night"] = RegularDayNight ?? 0,
                    ["Regular OT Night"] = RegularOTNight ?? 0,
                    ["Rest Day"] = RestDay ?? 0,
                    ["Rest Day OT"] = RestDayOT ?? 0,
                    ["Rest Day Night"] = RestDayNight ?? 0,
                    ["Rest Day OT Night"] = RestDayOTNight ?? 0,
                    ["Legal Holiday"] = LegalHoliday ?? 0,
                    ["Legal Holiday OT"] = LegalHolidayOT ?? 0,
                    ["Legal Holiday Night"] = LegalHolidayNight ?? 0,
                    ["Legal Holiday OT Night"] = LegalHolidayOTNight ?? 0,
                    ["Rest Day Legal Holiday"] = RestDayLegalHoliday ?? 0,
                    ["Rest Day Legal OT"] = RestDayLegalOT ?? 0,
                    ["Rest Day Legal Night"] = RestDayLegalNight ?? 0,
                    ["Rest Day Legal OT Night"] = RestDayLegalOTNight ?? 0,
                    ["Special Holiday"] = SpecialHoliday ?? 0,
                    ["Special Holiday OT"] = SpecialHolidayOT ?? 0,
                    ["Special Holiday Night"] = SpecialHolidayNight ?? 0,
                    ["Special Holiday OT Night"] = SpecialHolidayOTNight ?? 0,
                    ["Rest Day Special Holiday"] = RestDaySpecialHoliday ?? 0,
                    ["Rest Day Special OT"] = RestDaySpecialOT ?? 0,
                    ["Rest Day Special Night"] = RestDaySpecialNight ?? 0,
                    ["Rest Day Special OT Night"] = RestDaySpecialOTNight ?? 0,
                };

                // Only include non-zero entries to keep token count lean
                var nonZero = entries
                    .Where(kv => kv.Value > 0)
                    .ToDictionary(kv => kv.Key, kv => kv.Value);

                return nonZero.Count > 0 ? nonZero : null;
            }
        }
    }
}
