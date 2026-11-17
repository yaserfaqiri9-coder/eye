<script setup lang="ts">
/**
 * Dedicated soft 3D artwork for catalog entries. The SVG fallback keeps the existing
 * overview and statistic illustrations available to other report components.
 */
defineProps<{ name: string }>()
const reportAvatarImageSources: Record<string, string> = {
  patients: '/images/reports/patients-soft3d.png',
  doctors: '/images/reports/doctors-soft3d.png',
  surgeries: '/images/reports/surgeries-soft3d.png',
  diagnostics: '/images/reports/diagnostics-soft3d.png',
  diseases: '/images/reports/diseases-soft3d.png',
  cash: '/images/reports/cash-soft3d.png',
  outstanding: '/images/reports/outstanding-soft3d.png',
  expenses: '/images/reports/expenses-soft3d.png',
  'profit-loss': '/images/reports/sales-soft3d.png',
  'revenue-sources': '/images/reports/doctors-soft3d.png',
  'expense-summary': '/images/reports/expenses-soft3d.png',
  'daily-finance': '/images/reports/sales-soft3d.png',
  'cash-flow': '/images/reports/cash-soft3d.png',
  sales: '/images/reports/sales-soft3d.png',
  stock: '/images/reports/stock-soft3d.png',
}
</script>

<template>
  <img v-if="Object.prototype.hasOwnProperty.call(reportAvatarImageSources, name)" :src="reportAvatarImageSources[name]" class="report-avatar-image" alt="" aria-hidden="true" width="120" height="120" decoding="async" loading="lazy" draggable="false" />
  <svg v-else class="report-avatar" viewBox="0 0 120 120" aria-hidden="true" focusable="false">
    <circle cx="60" cy="60" r="46" fill="url(#ra-bg)" />
    <ellipse cx="60" cy="97" rx="32" ry="4" fill="#c7d3e6" opacity="0.6" />

    <!-- Figures in general (used by a figure card): a board of bars beside a ring chart -->
    <g v-if="name === 'overview'">
      <g filter="url(#ra-shadow)">
        <rect x="28" y="28" width="50" height="64" rx="6" fill="url(#ra-paper)" stroke="#d3ddeb" stroke-width="0.8" />
        <rect x="43" y="24" width="20" height="9" rx="3.5" fill="url(#ra-blue)" />
      </g>
      <rect x="35" y="39" width="27" height="3.2" rx="1.6" fill="#c4d4ec" />
      <rect x="35" y="45.5" width="18" height="3.2" rx="1.6" fill="#dbe5f3" />
      <rect x="36" y="70" width="6.5" height="15" rx="2" fill="url(#ra-blue-soft)" />
      <rect x="45.5" y="61" width="6.5" height="24" rx="2" fill="url(#ra-blue)" />
      <rect x="55" y="66" width="6.5" height="19" rx="2" fill="url(#ra-blue-soft)" />
      <rect x="64.5" y="55" width="6.5" height="30" rx="2" fill="url(#ra-blue)" />
      <g filter="url(#ra-shadow)">
        <circle cx="86" cy="46" r="15" fill="#fff" />
      </g>
      <circle cx="86" cy="46" r="9" fill="none" stroke="#dce6f5" stroke-width="5" />
      <circle cx="86" cy="46" r="9" fill="none" stroke="url(#ra-blue)" stroke-width="5" stroke-dasharray="34 57" transform="rotate(-90 86 46)" />
      <circle cx="86" cy="46" r="9" fill="none" stroke="url(#ra-orange)" stroke-width="5" stroke-dasharray="12 57" stroke-dashoffset="-34" transform="rotate(-90 86 46)" />
    </g>

    <!-- مراجعات مریضان: two patients and a health badge -->
    <g v-else-if="name === 'patients'">
      <path d="M62 96c0-13 7.5-20 18.5-20S99 83 99 96z" fill="url(#ra-blue-soft)" />
      <rect x="76.5" y="66" width="8" height="10" rx="3" fill="url(#ra-skin)" />
      <circle cx="80.5" cy="59" r="10" fill="url(#ra-skin)" />
      <path d="M70 60c-1-10 4.5-14 10.5-14S92 50 91 60c-1-1-2.5-5-6-6.5-3 2.5-8 4-15 6.5z" fill="#5a3a2a" />
      <path d="M26 96c0-16 9.5-25 24-25s24 9 24 25z" fill="url(#ra-navy)" />
      <path d="M44 71.5l6 9 6-9z" fill="#fff" />
      <rect x="45.5" y="61" width="9" height="11" rx="3" fill="url(#ra-skin)" />
      <circle cx="50" cy="52" r="12" fill="url(#ra-skin)" />
      <path d="M38 51.5c-.8-10.5 5-15.5 12.5-15.5 8 0 12.5 5.5 11.5 14-4-4.5-10-5.5-16-4-3.5 1-6.5 3-8 5.5z" fill="#2f241e" />
      <g filter="url(#ra-shadow)">
        <rect x="22" y="28" width="20" height="20" rx="6" fill="#fff" />
      </g>
      <path d="M30 32.5h4v5.5h5.5v4H34v5.5h-4V42h-5.5v-4H30z" fill="url(#ra-green)" transform="translate(0 -2)" />
    </g>

    <!-- کار داکتران: a doctor in a white coat with a stethoscope -->
    <g v-else-if="name === 'doctors'">
      <path d="M27 97c0-18 13-27 33-27s33 9 33 27z" fill="url(#ra-paper)" stroke="#d1dbe9" stroke-width="0.8" />
      <path d="M52 70.5l8 15 8-15z" fill="url(#ra-blue)" />
      <path d="M52 70.5l-6 26M68 70.5l6 26" stroke="#d1dbe9" stroke-width="1" fill="none" />
      <rect x="55" y="61" width="10" height="11" rx="3.5" fill="url(#ra-skin)" />
      <circle cx="60" cy="50" r="13" fill="url(#ra-skin)" />
      <path d="M47 49.5c-1-11.5 5.5-16.5 13.5-16.5 8.5 0 13.5 6 12.5 15.5-4-5.5-10.5-6.5-17-5-4 1-7.5 3-9 6z" fill="#2f241e" />
      <path d="M50.5 73c-4.5 9-1.5 17.5 5 17.5s8-6 7.5-9.5" fill="none" stroke="#2c3d58" stroke-width="2" stroke-linecap="round" />
      <circle cx="63" cy="81" r="3.2" fill="url(#ra-steel)" stroke="#8796ad" stroke-width="0.8" />
      <rect x="71" y="80" width="9" height="2.4" rx="1.2" fill="url(#ra-blue)" />
      <circle cx="88" cy="36" r="9.5" fill="url(#ra-green)" filter="url(#ra-shadow)" />
      <path d="M86.5 31.5h3v3h3v3h-3v3h-3v-3h-3v-3h3z" fill="#fff" />
    </g>

    <!-- آزمایش و عکس‌برداری: test tubes in a rack and a flask -->
    <g v-else-if="name === 'diagnostics'">
      <rect x="31" y="40" width="9" height="40" rx="4.5" fill="url(#ra-glass)" stroke="#b6cbe7" stroke-width="0.8" />
      <rect x="31" y="58" width="9" height="22" rx="4.5" fill="url(#ra-blue)" />
      <rect x="45" y="36" width="9" height="44" rx="4.5" fill="url(#ra-glass)" stroke="#b6cbe7" stroke-width="0.8" />
      <rect x="45" y="52" width="9" height="28" rx="4.5" fill="url(#ra-green)" />
      <rect x="59" y="42" width="9" height="38" rx="4.5" fill="url(#ra-glass)" stroke="#b6cbe7" stroke-width="0.8" />
      <rect x="59" y="63" width="9" height="17" rx="4.5" fill="url(#ra-orange)" />
      <g filter="url(#ra-shadow)">
        <rect x="26" y="68" width="47" height="7" rx="2.5" fill="url(#ra-blue)" />
        <rect x="29" y="75" width="4" height="18" rx="1.5" fill="url(#ra-navy)" />
        <rect x="66" y="75" width="4" height="18" rx="1.5" fill="url(#ra-navy)" />
      </g>
      <g filter="url(#ra-shadow)">
        <path d="M82 50v13l-9.5 21c-1.4 3.2.8 6.5 4.3 6.5h21.4c3.5 0 5.7-3.3 4.3-6.5L93 63V50z" fill="url(#ra-glass)" stroke="#b6cbe7" stroke-width="0.8" />
      </g>
      <path d="M76.5 75h23.5l2.5 9c1.3 3.2-.8 6.5-4.3 6.5H77.3c-3.5 0-5.7-3.3-4.3-6.5z" fill="url(#ra-blue-soft)" />
      <rect x="80" y="46.5" width="15" height="4.5" rx="2" fill="url(#ra-steel)" />
      <circle cx="84" cy="69" r="1.6" fill="#97bff2" />
      <circle cx="90" cy="64" r="1.1" fill="#97bff2" />
    </g>

    <!-- عملیات‌ها: a heart monitor and an eye in front of it -->
    <g v-else-if="name === 'surgeries'">
      <g filter="url(#ra-shadow)">
        <rect x="22" y="32" width="60" height="42" rx="6" fill="url(#ra-blue)" />
        <rect x="52" y="74" width="8" height="10" fill="url(#ra-steel)" />
        <rect x="42" y="84" width="28" height="5" rx="2.5" fill="url(#ra-steel)" />
      </g>
      <rect x="26.5" y="36.5" width="51" height="33" rx="3.5" fill="#123166" />
      <polyline points="29,56 40,56 44,47 49,64 53,51 56,56 75,56" fill="none" stroke="#4fe3aa" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round" />
      <rect x="31" y="41" width="12" height="2.4" rx="1.2" fill="#3e6cbf" />
      <g filter="url(#ra-shadow)">
        <circle cx="86" cy="73" r="15.5" fill="#fff" />
      </g>
      <path d="M73.5 73c4-7 21-7 25 0-4 7-21 7-25 0z" fill="#fff" stroke="url(#ra-blue)" stroke-width="1.8" />
      <circle cx="86" cy="73" r="5.2" fill="url(#ra-blue)" />
      <circle cx="86" cy="73" r="2.2" fill="#0f2a5c" />
      <circle cx="87.6" cy="71.4" r="1.1" fill="#fff" />
    </g>

    <!-- بیماری‌های شایع: a checklist under a magnifier that finds a germ -->
    <g v-else-if="name === 'diseases'">
      <g filter="url(#ra-shadow)">
        <rect x="27" y="28" width="44" height="60" rx="6" fill="url(#ra-paper)" stroke="#d3ddeb" stroke-width="0.8" />
        <rect x="39" y="24" width="20" height="8" rx="3.5" fill="url(#ra-blue)" />
      </g>
      <circle cx="35" cy="43" r="2.6" fill="url(#ra-green)" />
      <rect x="41" y="41.5" width="22" height="3" rx="1.5" fill="#c4d4ec" />
      <circle cx="35" cy="53" r="2.6" fill="url(#ra-red)" />
      <rect x="41" y="51.5" width="18" height="3" rx="1.5" fill="#c4d4ec" />
      <circle cx="35" cy="63" r="2.6" fill="url(#ra-green)" />
      <rect x="41" y="61.5" width="14" height="3" rx="1.5" fill="#dbe5f3" />
      <circle cx="35" cy="73" r="2.6" fill="url(#ra-orange)" />
      <rect x="41" y="71.5" width="10" height="3" rx="1.5" fill="#dbe5f3" />
      <line x1="87" y1="76" x2="98" y2="88" stroke="url(#ra-navy)" stroke-width="6.5" stroke-linecap="round" />
      <circle cx="78" cy="66" r="14" fill="#eef5ff" fill-opacity="0.85" stroke="url(#ra-blue)" stroke-width="4" filter="url(#ra-shadow)" />
      <g fill="url(#ra-red)">
        <circle cx="78" cy="66" r="5" />
        <circle cx="85.5" cy="66" r="1.5" />
        <circle cx="83.3" cy="71.3" r="1.5" />
        <circle cx="78" cy="73.5" r="1.5" />
        <circle cx="72.7" cy="71.3" r="1.5" />
        <circle cx="70.5" cy="66" r="1.5" />
        <circle cx="72.7" cy="60.7" r="1.5" />
        <circle cx="78" cy="58.5" r="1.5" />
        <circle cx="83.3" cy="60.7" r="1.5" />
      </g>
    </g>

    <!-- Income (used by a figure card): a sheet with a rising arrow and a coin -->
    <g v-else-if="name === 'income'">
      <g filter="url(#ra-shadow)">
        <rect x="26" y="30" width="44" height="58" rx="6" fill="url(#ra-paper)" stroke="#d3ddeb" stroke-width="0.8" />
      </g>
      <rect x="33" y="38" width="22" height="3.2" rx="1.6" fill="#c4d4ec" />
      <rect x="33" y="44.5" width="14" height="3.2" rx="1.6" fill="#dbe5f3" />
      <rect x="34" y="72" width="5.5" height="10" rx="1.5" fill="url(#ra-blue-soft)" />
      <rect x="42" y="66" width="5.5" height="16" rx="1.5" fill="url(#ra-blue-soft)" />
      <rect x="50" y="69" width="5.5" height="13" rx="1.5" fill="url(#ra-blue-soft)" />
      <rect x="58" y="60" width="5.5" height="22" rx="1.5" fill="url(#ra-blue-soft)" />
      <polyline points="31,74 43,61 51,66 79,39" fill="none" stroke="url(#ra-green)" stroke-width="4.2" stroke-linecap="round" stroke-linejoin="round" />
      <path d="M73 35.5l11-2.5-2.5 11z" fill="#14a26c" stroke="#14a26c" stroke-width="2" stroke-linejoin="round" />
      <g filter="url(#ra-shadow)">
        <circle cx="82" cy="76" r="13" fill="url(#ra-coin)" />
      </g>
      <circle cx="82" cy="76" r="9.5" fill="none" stroke="#fff" stroke-opacity="0.55" stroke-width="1.2" />
      <text x="82" y="81" text-anchor="middle" font-size="13" font-weight="700" fill="#fff">؋</text>
    </g>

    <!-- دریافت‌ها: a safe with notes and coins -->
    <g v-else-if="name === 'cash'">
      <g filter="url(#ra-shadow)">
        <rect x="26" y="34" width="56" height="54" rx="7" fill="url(#ra-steel)" stroke="#a9b7cb" stroke-width="0.8" />
      </g>
      <rect x="31" y="39" width="46" height="44" rx="5" fill="url(#ra-blue)" />
      <circle cx="51" cy="61" r="10" fill="url(#ra-steel)" />
      <circle cx="51" cy="61" r="6" fill="none" stroke="#7d8ea8" stroke-width="1.4" />
      <path d="M51 53.5v3M51 65.5v3M43.5 61h3M55.5 61h3" stroke="#7d8ea8" stroke-width="1.4" stroke-linecap="round" />
      <rect x="67" y="55" width="3.5" height="12" rx="1.75" fill="#fff" />
      <rect x="31" y="88" width="7" height="4" rx="1.5" fill="#8a99b0" />
      <rect x="70" y="88" width="7" height="4" rx="1.5" fill="#8a99b0" />
      <g transform="rotate(-10 86 74)" filter="url(#ra-shadow)">
        <rect x="70" y="66" width="32" height="17" rx="2.5" fill="url(#ra-cash)" />
        <rect x="73" y="69" width="26" height="11" rx="1.5" fill="none" stroke="#fff" stroke-opacity="0.6" stroke-width="1" />
        <circle cx="86" cy="74.5" r="3.2" fill="#fff" fill-opacity="0.7" />
      </g>
      <ellipse cx="22" cy="88" rx="7" ry="2.6" fill="#d9951a" />
      <rect x="15" y="82" width="14" height="6" fill="url(#ra-coin)" />
      <ellipse cx="22" cy="82" rx="7" ry="2.6" fill="#ffe08f" />
    </g>

    <!-- طلبات: an unpaid bill and a clock -->
    <g v-else-if="name === 'outstanding'">
      <g filter="url(#ra-shadow)">
        <rect x="28" y="27" width="44" height="62" rx="6" fill="url(#ra-paper)" stroke="#d3ddeb" stroke-width="0.8" />
      </g>
      <rect x="35" y="35" width="18" height="4" rx="2" fill="url(#ra-blue)" />
      <rect x="35" y="45" width="30" height="2.8" rx="1.4" fill="#c4d4ec" />
      <rect x="35" y="51.5" width="24" height="2.8" rx="1.4" fill="#dbe5f3" />
      <rect x="35" y="58" width="27" height="2.8" rx="1.4" fill="#dbe5f3" />
      <rect x="35" y="68" width="30" height="0.9" fill="#c4d4ec" />
      <rect x="35" y="72.5" width="14" height="6" rx="3" fill="url(#ra-red)" />
      <g filter="url(#ra-shadow)">
        <circle cx="81" cy="71" r="15.5" fill="url(#ra-orange)" />
      </g>
      <circle cx="81" cy="71" r="11.5" fill="#fff" />
      <path d="M81 71v-7.5M81 71l5.5 3.2" stroke="#2c3d58" stroke-width="2.2" stroke-linecap="round" />
      <circle cx="81" cy="71" r="1.6" fill="#2c3d58" />
    </g>

    <!-- Discounts and refunds (used by a figure card): a bill, a refund arrow and a percent tag -->
    <g v-else-if="name === 'adjustments'">
      <g filter="url(#ra-shadow)">
        <rect x="30" y="27" width="44" height="62" rx="6" fill="url(#ra-paper)" stroke="#d3ddeb" stroke-width="0.8" />
      </g>
      <rect x="37" y="36" width="18" height="4" rx="2" fill="url(#ra-blue)" />
      <rect x="37" y="46" width="30" height="2.8" rx="1.4" fill="#c4d4ec" />
      <rect x="37" y="52.5" width="22" height="2.8" rx="1.4" fill="#dbe5f3" />
      <rect x="37" y="59" width="27" height="2.8" rx="1.4" fill="#dbe5f3" />
      <rect x="37" y="66" width="16" height="2.8" rx="1.4" fill="#dbe5f3" />
      <g filter="url(#ra-shadow)">
        <circle cx="82" cy="72" r="15" fill="url(#ra-blue)" />
      </g>
      <path d="M76 67.5a8 8 0 1 1-1.2 8.6" fill="none" stroke="#fff" stroke-width="2.6" stroke-linecap="round" />
      <path d="M72 62.5l4.6 6.2-7.6.8z" fill="#fff" />
      <g transform="rotate(-14 30 40)" filter="url(#ra-shadow)">
        <path d="M19 33h17l5 7-5 7H19a3 3 0 0 1-3-3v-8a3 3 0 0 1 3-3z" fill="url(#ra-red)" />
        <text x="27" y="44.2" text-anchor="middle" font-size="10" font-weight="700" fill="#fff">٪</text>
      </g>
    </g>

    <!-- مصارف: a receipt, a card and notes going into a wallet -->
    <g v-else-if="name === 'expenses'">
      <g filter="url(#ra-shadow)">
        <path d="M38 24h34v46l-4.25-3-4.25 3-4.25-3-4.25 3-4.25-3-4.25 3-4.25-3-4.25 3z" fill="url(#ra-paper)" stroke="#d3ddeb" stroke-width="0.8" />
      </g>
      <rect x="44" y="31" width="16" height="3.4" rx="1.7" fill="url(#ra-blue)" />
      <rect x="44" y="39" width="22" height="2.6" rx="1.3" fill="#c4d4ec" />
      <rect x="44" y="45" width="18" height="2.6" rx="1.3" fill="#dbe5f3" />
      <rect x="44" y="51" width="20" height="2.6" rx="1.3" fill="#dbe5f3" />
      <rect x="31" y="50" width="34" height="18" rx="2.5" fill="url(#ra-cash)" transform="rotate(8 48 59)" />
      <rect x="40" y="49" width="38" height="23" rx="3.5" fill="url(#ra-orange)" transform="rotate(-9 59 60)" />
      <g filter="url(#ra-shadow)">
        <rect x="26" y="60" width="62" height="34" rx="7" fill="url(#ra-blue)" />
      </g>
      <rect x="64" y="69" width="28" height="15" rx="5" fill="url(#ra-navy)" />
      <circle cx="71.5" cy="76.5" r="2.6" fill="#fff" />
      <circle cx="91" cy="36" r="9.5" fill="url(#ra-red)" filter="url(#ra-shadow)" />
      <path d="M91 31v9.5M86.8 36.8L91 41l4.2-4.2" fill="none" stroke="#fff" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round" />
    </g>

    <!-- فروش دواخانه و عینک: a pharmacy bag, a capsule and spectacles -->
    <g v-else-if="name === 'sales'">
      <path d="M45 50c0-13 22-13 22 0" fill="none" stroke="#1a4ea6" stroke-width="3.2" stroke-linecap="round" />
      <g filter="url(#ra-shadow)">
        <path d="M33 48h46l4 42a3 3 0 0 1-3 3.3H32a3 3 0 0 1-3-3.3z" fill="url(#ra-blue)" />
      </g>
      <rect x="33" y="48" width="46" height="5" fill="#1d58b9" opacity="0.55" />
      <path d="M53 61h6v6h6v6h-6v6h-6v-6h-6v-6h6z" fill="#fff" />
      <g transform="rotate(-38 88 58)" filter="url(#ra-shadow)">
        <rect x="76" y="52" width="24" height="12" rx="6" fill="#fff" stroke="#d0d9e6" stroke-width="0.8" />
        <path d="M82 52h6v12h-6a6 6 0 0 1 0-12z" fill="url(#ra-red)" />
      </g>
      <g filter="url(#ra-shadow)">
        <circle cx="76" cy="85" r="6.5" fill="url(#ra-glass)" stroke="#2c3d58" stroke-width="2.4" />
        <circle cx="93" cy="85" r="6.5" fill="url(#ra-glass)" stroke="#2c3d58" stroke-width="2.4" />
        <path d="M82.5 84c1.2-1.6 3.8-1.6 4 0" fill="none" stroke="#2c3d58" stroke-width="2.2" stroke-linecap="round" />
      </g>
    </g>

    <!-- موجودی گدام: stacked cartons and a check -->
    <g v-else-if="name === 'stock'">
      <g filter="url(#ra-shadow)">
        <rect x="25" y="62" width="34" height="31" rx="3" fill="url(#ra-carton)" stroke="#b9874e" stroke-width="0.8" />
        <rect x="61" y="62" width="34" height="31" rx="3" fill="url(#ra-carton)" stroke="#b9874e" stroke-width="0.8" />
        <rect x="42" y="33" width="34" height="29" rx="3" fill="url(#ra-carton)" stroke="#b9874e" stroke-width="0.8" />
      </g>
      <rect x="39" y="62" width="6" height="11" fill="#f6e2bf" />
      <rect x="75" y="62" width="6" height="11" fill="#f6e2bf" />
      <rect x="56" y="33" width="6" height="10" fill="#f6e2bf" />
      <rect x="29" y="82" width="12" height="7" rx="1" fill="#fff" opacity="0.85" />
      <rect x="65" y="82" width="12" height="7" rx="1" fill="#fff" opacity="0.85" />
      <rect x="46" y="52" width="12" height="7" rx="1" fill="#fff" opacity="0.85" />
      <circle cx="88" cy="40" r="10" fill="url(#ra-blue)" filter="url(#ra-shadow)" />
      <path d="M83.5 40.2l3.2 3.2 5.8-6.4" fill="none" stroke="#fff" stroke-width="2.6" stroke-linecap="round" stroke-linejoin="round" />
    </g>

    <!-- Any report without a picture of its own: a plain sheet. -->
    <g v-else>
      <g filter="url(#ra-shadow)">
        <rect x="36" y="28" width="48" height="62" rx="6" fill="url(#ra-paper)" stroke="#d3ddeb" stroke-width="0.8" />
      </g>
      <rect x="44" y="38" width="22" height="4" rx="2" fill="url(#ra-blue)" />
      <rect x="44" y="49" width="32" height="2.8" rx="1.4" fill="#c4d4ec" />
      <rect x="44" y="56" width="26" height="2.8" rx="1.4" fill="#dbe5f3" />
      <rect x="44" y="63" width="29" height="2.8" rx="1.4" fill="#dbe5f3" />
      <rect x="44" y="70" width="18" height="2.8" rx="1.4" fill="#dbe5f3" />
    </g>
  </svg>
</template>

<style scoped>
.report-avatar-image {
  display: block;
  width: 100%;
  height: 100%;
  object-fit: contain;
  user-select: none;
  pointer-events: none;
}
.report-avatar {
  display: block;
  width: 100%;
  height: 100%;
  overflow: visible;
}
</style>
