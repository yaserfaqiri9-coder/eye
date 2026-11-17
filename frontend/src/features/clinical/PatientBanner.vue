<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { api } from '@/api/http'
import { genderLabels, type Patient } from '@/features/patients/types'
import { visitStatusLabels, visitStatusTones, type VisitRow } from '@/features/visits/types'
import { useAuthStore } from '@/stores/auth'
import { formatDate } from '@/utils/date'
import { quantity } from '@/utils/money'

/**
 * `allergy` is what the patient reacts to, `conditions` the systemic diseases they answered yes to.
 * Both sit beside the name so they are read before anything is prescribed.
 */
const props = defineProps<{ visit: VisitRow; allergy?: string | null; conditions?: string[] }>()
const auth = useAuthStore()
const patient = ref<Patient | null>(null)

async function load() {
  // Age and gender are a convenience here; the form still works without them.
  patient.value = auth.can('patients.view') ? await api<Patient>(`/patients/${props.visit.patientId}`).catch(() => null) : null
}

const details = computed(() => {
  const p = patient.value
  if (!p) return ''
  return [p.ageYears !== null ? `${quantity(p.ageYears)} ساله` : null, genderLabels[p.gender]].filter(Boolean).join('، ')
})

watch(() => props.visit.patientId, load)
onMounted(load)
</script>

<template>
  <section class="xr-card banner">
    <div class="banner__who">
      <h1 class="banner__name">{{ visit.patientName }}</h1>
      <span v-if="details" class="banner__details">{{ details }}</span>
      <span class="xr-badge" :class="`xr-badge--${visitStatusTones[visit.status]}`">{{ visitStatusLabels[visit.status] }}</span>
    </div>
    <dl class="xr-facts banner__facts">
      <div>
        <dt>شماره پرونده</dt>
        <dd>
          <RouterLink v-if="auth.can('patients.view')" :to="`/patients/${visit.patientId}`"><span class="ltr-num">{{ visit.patientFileNumber }}</span></RouterLink>
          <span v-else class="ltr-num">{{ visit.patientFileNumber }}</span>
        </dd>
      </div>
      <div>
        <dt>مراجعه</dt>
        <dd><span class="ltr-num">{{ visit.visitNumber }}</span></dd>
      </div>
      <div>
        <dt>تاریخ</dt>
        <dd>{{ formatDate(visit.visitDate) }}</dd>
      </div>
      <div>
        <dt>علت مراجعه</dt>
        <dd>{{ visit.reason ?? '—' }}</dd>
      </div>
    </dl>
    <div v-if="allergy != null || conditions?.length" class="banner__alerts">
      <span v-if="allergy != null" class="banner__alert banner__alert--danger" role="alert">
        <i class="ri-alert-fill" aria-hidden="true"></i> حساسیت: <bdi>{{ allergy || 'دارد (نوع آن ثبت نشده)' }}</bdi>
      </span>
      <span v-if="conditions?.length" class="banner__alert">
        <i class="ri-heart-pulse-line" aria-hidden="true"></i> بیماری‌ها: <bdi dir="ltr">{{ conditions.join(', ') }}</bdi>
      </span>
    </div>
  </section>
</template>

<style scoped>
.banner {
  display: grid;
  grid-template-columns: minmax(220px, auto) minmax(0, 1fr);
  align-items: center;
  gap: 16px 32px;
  padding: 16px 20px;
}

.banner__who {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 4px 12px;
}

.banner__name {
  font-size: var(--xr-fs-card-title);
}

.banner__details {
  color: var(--xr-text-muted);
}

.banner__alerts {
  display: flex;
  flex-wrap: wrap;
  grid-column: 1 / -1;
  gap: 8px;
}

.banner__alert {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 4px 10px;
  border-radius: 6px;
  background: color-mix(in srgb, var(--xr-status-orange) 14%, transparent);
  color: var(--xr-text);
  font-size: 13px;
  font-weight: 600;
}

.banner__alert--danger {
  background: color-mix(in srgb, var(--xr-status-red) 16%, transparent);
  color: var(--xr-status-red);
}

@media (max-width: 991.98px) {
  .banner {
    grid-template-columns: minmax(0, 1fr);
  }
}
</style>
