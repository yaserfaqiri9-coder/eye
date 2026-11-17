<!--
  The gradients and the soft shadow every report picture shares. Rendered once per page in a zero-size
  svg (never display:none, which makes some browsers drop the gradients), so the pictures stay small.
-->
<template>
  <svg class="report-art-defs" width="0" height="0" aria-hidden="true" focusable="false">
    <defs>
      <radialGradient id="ra-bg" cx="50%" cy="38%" r="62%">
        <stop offset="0" stop-color="#ffffff" />
        <stop offset="1" stop-color="#e3e9f3" />
      </radialGradient>
      <linearGradient id="ra-blue" x1="0" y1="0" x2="0" y2="1">
        <stop offset="0" stop-color="#5ea2f6" />
        <stop offset="1" stop-color="#1c5cc6" />
      </linearGradient>
      <linearGradient id="ra-blue-soft" x1="0" y1="0" x2="0" y2="1">
        <stop offset="0" stop-color="#d4e5fc" />
        <stop offset="1" stop-color="#97bff2" />
      </linearGradient>
      <linearGradient id="ra-navy" x1="0" y1="0" x2="0" y2="1">
        <stop offset="0" stop-color="#3a72d6" />
        <stop offset="1" stop-color="#173f8f" />
      </linearGradient>
      <linearGradient id="ra-paper" x1="0" y1="0" x2="1" y2="1">
        <stop offset="0" stop-color="#ffffff" />
        <stop offset="1" stop-color="#e8eef7" />
      </linearGradient>
      <linearGradient id="ra-coin" x1="0" y1="0" x2="0" y2="1">
        <stop offset="0" stop-color="#ffdb7d" />
        <stop offset="1" stop-color="#e09616" />
      </linearGradient>
      <linearGradient id="ra-green" x1="0" y1="0" x2="0" y2="1">
        <stop offset="0" stop-color="#52dba4" />
        <stop offset="1" stop-color="#12a06a" />
      </linearGradient>
      <linearGradient id="ra-orange" x1="0" y1="0" x2="0" y2="1">
        <stop offset="0" stop-color="#ffc55e" />
        <stop offset="1" stop-color="#f0871b" />
      </linearGradient>
      <linearGradient id="ra-red" x1="0" y1="0" x2="0" y2="1">
        <stop offset="0" stop-color="#ff8080" />
        <stop offset="1" stop-color="#de3a3a" />
      </linearGradient>
      <linearGradient id="ra-steel" x1="0" y1="0" x2="0" y2="1">
        <stop offset="0" stop-color="#f3f6fa" />
        <stop offset="1" stop-color="#b5c2d4" />
      </linearGradient>
      <linearGradient id="ra-skin" x1="0" y1="0" x2="0" y2="1">
        <stop offset="0" stop-color="#fadcc0" />
        <stop offset="1" stop-color="#e8b28a" />
      </linearGradient>
      <linearGradient id="ra-glass" x1="0" y1="0" x2="1" y2="1">
        <stop offset="0" stop-color="#f4f9ff" />
        <stop offset="1" stop-color="#cfe2f8" />
      </linearGradient>
      <linearGradient id="ra-carton" x1="0" y1="0" x2="0" y2="1">
        <stop offset="0" stop-color="#efcf9c" />
        <stop offset="1" stop-color="#cf9a5c" />
      </linearGradient>
      <linearGradient id="ra-cash" x1="0" y1="0" x2="1" y2="1">
        <stop offset="0" stop-color="#8fe3b7" />
        <stop offset="1" stop-color="#3dad78" />
      </linearGradient>
      <filter id="ra-shadow" x="-30%" y="-30%" width="160%" height="160%">
        <feDropShadow dx="0" dy="2.2" stdDeviation="2.2" flood-color="#4d6a96" flood-opacity="0.22" />
      </filter>
    </defs>
  </svg>
</template>

<style scoped>
.report-art-defs {
  position: absolute;
  width: 0;
  height: 0;
  overflow: hidden;
  pointer-events: none;
}
</style>
