from pathlib import Path
p=Path(r'C:\Visual Studio 2026\Projetos\Fuga\FugaTES\FugaTES_DEV')/'src/FugaTES.Aplicacao/Transportadoras/CarrierManualReadOnly.cs'
s=p.read_text(encoding='utf-8')
old="""            // Conservative uncertainty envelope, not a civil-date business rule.
            // Covers exact instants and civil-day/offset interpretations without choosing a timezone
            // or silently extending ValidTo. Near either bound no eligibility is asserted.
            var sinceStart=(reference-from).TotalDays;var untilEnd=(to-reference).TotalDays;
            if(sinceStart>3&&untilEnd>3)return 1;
            if(sinceStart>=-3&&untilEnd>=-3)uncertain=true;"""
new="""            // Compare all supported interpretations: exact UTC instants and civil dates
            // represented in UTC or the explicit wire offset, across offsets [-14h,+14h].
            // This is an uncertainty envelope, not an adopted SAP timezone/end-of-day rule.
            var start=Bounds(from,false);var end=Bounds(to,true);
            if(reference>=start.Latest&&reference<end.Earliest)return 1;
            if(reference>=start.Earliest&&reference<=end.Latest)uncertain=true;"""
assert old in s;s=s.replace(old,new)
at=s.rfind('}')
s=s[:at]+"""    static (DateTimeOffset Earliest,DateTimeOffset Latest) Bounds(DateTimeOffset value,bool end)
    {
        var values=new List<DateTimeOffset>{value};
        foreach(var date in new[]{value.UtcDateTime.Date,value.Date})
        {
            var ticks=date.Ticks+(end?TimeSpan.TicksPerDay:0);
            foreach(var shift in new[]{-14,14})
                values.Add(new DateTimeOffset(Math.Clamp(ticks+TimeSpan.FromHours(shift).Ticks,DateTimeOffset.MinValue.Ticks,DateTimeOffset.MaxValue.Ticks),TimeSpan.Zero));
        }
        return(values.Min(),values.Max());
    }
"""+s[at:]
p.write_text(s,encoding='utf-8')
