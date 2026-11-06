<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue'
import { api, errorMessage } from '@/api/http'
import UiComboBox, { type ComboOption } from '@/components/UiComboBox.vue'
import { useAuthStore } from '@/stores/auth'
import { formatDateTime } from '@/utils/date'
import { money, quantity } from '@/utils/money'
import type { SuppliesRecord, SupplyLocation, SupplyStock, SurgeryDetail, SurgeryPackage } from './types'

const props = defineProps<{ detail: SurgeryDetail }>()
const emit = defineEmits<{ changed: [] }>()
const auth = useAuthStore()

interface Line {
  productId: number
  name: string
  unit: string
  quantity: number
}

const record = ref<SuppliesRecord | null>(null)
const packages = ref<SurgeryPackage[]>([])
const locations = ref<SupplyLocation[]>([])
const onHand = ref<Record<number, number>>({})
const found = new Map<number, SupplyStock>()
const form = reactive({ packageId: null as number | null, location: '', lines: [] as Line[], adding: null as number | null })
const saving = ref(false)
const error = ref('')

/** Supplies are used in the theatre, so they are recorded once the operation has begun. */
const started = computed(() => ['InProgress', 'Completed', 'FollowUp'].includes(props.detail.case.status))
const canRecord = computed(() => auth.can('surgery.edit') && started.value && record.value?.recorded === false)
const chosenPackage = computed(() => packages.value.find((p) => p.id === form.packageId) ?? null)
const packageOptions = computed<ComboOption[]>(() => packages.value.map((p) => ({ value: p.id, label: p.name, hint: `${quantity(p.items.length)} قلم` })))
const short = computed(() => form.lines.filter((l) => l.quantity > (onHand.value[l.productId] ?? 0)))

function fill(pkg: SurgeryPackage | null) {
  form.lines = pkg ? pkg.items.map((i) => ({ productId: i.productId, name: i.productName, unit: i.unit, quantity: i.quantity })) : []
  if (pkg) form.location = pkg.locationCode
}

async function loadStock() {
  if (!form.location || !form.lines.length) return
  const ids = form.lines.map((l) => l.productId).join(',')
  const rows = await api<SupplyStock[]>(`/surgery-packages/stock?location=${encodeURIComponent(form.location)}&ids=${ids}`)
  onHand.value = Object.fromEntries(rows.map((r) => [r.productId, r.onHand]))
}

async function searchProducts(query: string, signal: AbortSignal): Promise<ComboOption[]> {
  if (!query || !form.location) return []
  const rows = await api<SupplyStock[]>(`/surgery-packages/stock?location=${encodeURIComponent(form.location)}&q=${encodeURIComponent(query)}`, { signal })
  rows.forEach((r) => found.set(r.productId, r))
  return rows.map((r) => ({ value: r.productId, label: r.name, hint: `موجود: ${quantity(r.onHand)} ${r.unit}` }))
}

function onAdd(option: ComboOption | null) {
  if (!option) return
  const id = option.value as number
  const existing = form.lines.find((l) => l.productId === id)
  if (existing) existing.quantity += 1
  else form.lines.push({ productId: id, name: option.label, unit: found.get(id)?.unit ?? '', quantity: 1 })
  form.adding = null
  loadStock().catch((cause) => (error.value = errorMessage(cause)))
}

async function submit() {
  const lines = form.lines.filter((l) => l.quantity > 0)
  const summary = lines.map((l) => `${l.name}: ${quantity(l.quantity)}`).join('\n')
  const where = locations.value.find((l) => l.code === form.location)?.name ?? form.location
  if (!window.confirm(`این اجناس از «${where}» کسر شود؟ پس از ثبت، فقط از بخش گدام برگشت می‌شود.\n\n${summary || 'هیچ جنسی'}`)) return
  saving.value = true
  error.value = ''
  try {
    record.value = await api<SuppliesRecord>(`/surgeries/${props.detail.case.id}/supplies`, {
      method: 'POST',
      body: { locationCode: form.location, items: lines.map((l) => ({ productId: l.productId, quantity: l.quantity })), version: props.detail.case.version },
    })
    emit('changed')
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    saving.value = false
  }
}

watch(() => form.packageId, (id) => {
  fill(packages.value.find((p) => p.id === id) ?? null)
  loadStock().catch((cause) => (error.value = errorMessage(cause)))
})
watch(() => form.location, () => loadStock().catch((cause) => (error.value = errorMessage(cause))))

onMounted(async () => {
  try {
    const [loadedRecord, loadedPackages, loadedLocations] = await Promise.all([
      api<SuppliesRecord>(`/surgeries/${props.detail.case.id}/supplies`),
      api<SurgeryPackage[]>('/surgery-packages'),
      api<SupplyLocation[]>('/surgery-packages/locations'),
    ])
    record.value = loadedRecord
    packages.value = loadedPackages
    locations.value = loadedLocations
    form.location = loadedLocations.find((l) => l.code === 'surgery')?.code ?? loadedLocations[0]?.code ?? ''
    form.packageId = props.detail.packageId
  } catch (cause) {
    error.value = errorMessage(cause)
  }
})
</script>

<template>
  <section class="supplies" aria-labelledby="supplies-title">
    <h2 id="supplies-title" class="h6 mb-1">مواد مصرفی عملیات</h2>
    <p class="form-hint">دوا و وسایل این عملیات داخل فیس آن است و جدا از مریض پول گرفته نمی‌شود. فقط از گدام کسر می‌شود.</p>
    <div v-if="error" class="alert alert-danger" role="alert">{{ error }}</div>

    <template v-if="record?.recorded">
      <div class="alert alert-success" role="status">
        ثبت شد و از «{{ record.locationName }}» کسر گردید — {{ formatDateTime(record.recordedAt) }}
      </div>
      <div class="xr-table-wrap">
        <table class="xr-table">
          <thead><tr><th>جنس</th><th>مقدار</th><th>قیمت تمام‌شده</th></tr></thead>
          <tbody>
            <tr v-for="i in record.items" :key="i.productId">
              <td>{{ i.productName }}</td>
              <td>{{ quantity(i.quantity) }} {{ i.unit }}</td>
              <td>{{ money(i.cost) }}</td>
            </tr>
          </tbody>
        </table>
      </div>
      <p class="form-hint mt-2 mb-0">اگر جنسی اشتباه ثبت شده، مسئول گدام آن را از «گردش‌ها» برگشت می‌دهد.</p>
    </template>

    <div v-else-if="record && !started" class="alert alert-info mb-0" role="status">
      <template v-if="detail.packageName">پکیج «{{ detail.packageName }}» برای این عملیات انتخاب شده است. </template>
      پس از شروع عملیات، مواد مصرفی همین‌جا ثبت و از گدام کسر می‌شود.
    </div>

    <template v-else-if="canRecord">
      <div class="row g-3 mb-3">
        <div class="col-md-6"><UiComboBox v-model="form.packageId" label="پکیج" :options="packageOptions" chip placeholder="بدون پکیج" /></div>
        <div class="col-md-6">
          <label class="form-label" for="sp-location">کسر از گدام</label>
          <select id="sp-location" v-model="form.location" class="form-select">
            <option v-for="l in locations" :key="l.code" :value="l.code">{{ l.name }}</option>
          </select>
        </div>
      </div>

      <div v-if="detail.packageId && chosenPackage === null" class="alert alert-warning">پکیج این عملیات دیگر فعال نیست. اجناس را دستی اضافه کنید.</div>

      <div v-if="form.lines.length" class="xr-table-wrap mb-3">
        <table class="xr-table">
          <thead><tr><th>جنس</th><th>موجود در گدام</th><th>مقدار مصرف</th><th><span class="visually-hidden">حذف</span></th></tr></thead>
          <tbody>
            <tr v-for="(line, index) in form.lines" :key="line.productId" :class="{ 'supplies__short': line.quantity > (onHand[line.productId] ?? 0) }">
              <td>{{ line.name }}</td>
              <td>{{ quantity(onHand[line.productId] ?? 0) }} {{ line.unit }}</td>
              <td><input v-model.number="line.quantity" class="form-control form-control-sm supplies__qty" type="number" min="0" step="any" :aria-label="`مقدار ${line.name}`" /></td>
              <td><button type="button" class="btn btn-quiet btn-sm text-danger" :aria-label="`حذف ${line.name}`" @click="form.lines.splice(index, 1)"><i class="ri-delete-bin-line" aria-hidden="true"></i></button></td>
            </tr>
          </tbody>
        </table>
      </div>
      <p v-else class="xr-state">جنسی انتخاب نشده است. پکیج را انتخاب کنید یا جنس اضافه کنید.</p>

      <div class="mb-3"><UiComboBox v-model="form.adding" label="افزودن جنس دیگر" :search="searchProducts" placeholder="نام جنس را تایپ کنید" @select="onAdd" /></div>

      <div v-if="short.length" class="alert alert-warning" role="alert">
        موجودی این اجناس در این گدام کافی نیست: {{ short.map((l) => l.name).join('، ') }}. گدام دیگری انتخاب کنید، مقدار را کم کنید یا جنس را به این گدام انتقال دهید.
      </div>

      <button type="button" class="btn btn-primary" :disabled="saving || short.length > 0" @click="submit">
        <i class="ri-inbox-unarchive-line" aria-hidden="true"></i> {{ saving ? 'در حال ثبت…' : 'ثبت مصرف و کسر از گدام' }}
      </button>
    </template>
  </section>
</template>

<style scoped>
.supplies__qty {
  width: 110px;
}

.supplies__short td {
  color: var(--xr-red);
}
</style>
