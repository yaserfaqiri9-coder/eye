<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { api, errorMessage } from '@/api/http'
import UiModal from '@/components/UiModal.vue'
import { useAuthStore } from '@/stores/auth'
import { money, quantity } from '@/utils/money'
import type { SupplyLocation, SurgeryPackage } from './types'

const auth = useAuthStore()
const canEdit = computed(() => auth.can('surgery.approve'))

const rows = ref<SurgeryPackage[]>([])
const locations = ref<SupplyLocation[]>([])
const loading = ref(true)
const error = ref('')

const editor = reactive({
  open: false,
  editing: null as SurgeryPackage | null,
  name: '',
  salePrice: null as number | null,
  costPrice: null as number | null,
  quantity: null as number | null,
  location: '',
  isActive: true,
  saving: false,
  error: '',
})

async function load() {
  try {
    rows.value = await api<SurgeryPackage[]>('/surgery-packages?all=true')
    error.value = ''
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    loading.value = false
  }
}

function open(pkg: SurgeryPackage | null) {
  Object.assign(editor, {
    open: true,
    editing: pkg,
    name: pkg?.name ?? '',
    salePrice: pkg?.price ?? null,
    costPrice: pkg?.costPrice ?? null,
    quantity: null,
    location: pkg?.locationCode ?? locations.value.find((l) => l.code === 'surgery')?.code ?? locations.value[0]?.code ?? '',
    isActive: pkg?.isActive ?? true,
    saving: false,
    error: '',
  })
}

async function save() {
  editor.saving = true
  editor.error = ''
  try {
    const body = {
      name: editor.name,
      salePrice: editor.salePrice || 0,
      costPrice: typeof editor.costPrice === 'number' ? editor.costPrice : null,
      quantity: editor.quantity || 0,
      locationCode: editor.location,
      isActive: editor.isActive,
      version: editor.editing?.version,
    }
    if (editor.editing) await api(`/surgery-packages/${editor.editing.id}`, { method: 'PUT', body })
    else await api('/surgery-packages', { method: 'POST', body })
    editor.open = false
    await load()
  } catch (cause) {
    editor.error = errorMessage(cause)
  } finally {
    editor.saving = false
  }
}

onMounted(async () => {
  try {
    locations.value = await api<SupplyLocation[]>('/surgery-packages/locations')
  } catch (cause) {
    error.value = errorMessage(cause)
  }
  await load()
})
</script>

<template>
  <div>
    <div class="d-flex flex-wrap align-items-start justify-content-between gap-2 mb-3">
      <p class="form-hint mb-0">هر پکیج با نام، قیمت خرید، قیمت فروش و تعداد ثبت می‌شود و همان تعداد به موجودی گدام اضافه می‌گردد. مریض قیمت فروش پکیج را می‌پردازد و پس از عملیات یک پکیج از موجودی کسر می‌شود.</p>
      <button v-if="canEdit" type="button" class="btn btn-primary text-nowrap" @click="open(null)"><i class="ri-add-line" aria-hidden="true"></i> پکیج جدید</button>
    </div>
    <div v-if="error" class="alert alert-danger" role="alert">{{ error }}</div>
    <div v-if="loading" class="xr-state" role="status">در حال بارگذاری...</div>
    <div v-else-if="rows.length === 0" class="xr-state">هنوز پکیجی ساخته نشده است.</div>
    <div v-else class="xr-table-wrap">
      <table class="xr-table">
        <thead><tr><th>پکیج</th><th>قیمت خرید</th><th>قیمت فروش</th><th>موجودی</th><th>وضعیت</th><th v-if="canEdit"><span class="visually-hidden">عملیات</span></th></tr></thead>
        <tbody>
          <tr v-for="p in rows" :key="p.id">
            <td><strong>{{ p.name }}</strong><div class="xr-meta">{{ p.locationName }}</div></td>
            <td>{{ p.costPrice === null ? '—' : money(p.costPrice) }}</td>
            <td>{{ p.price === null ? '—' : money(p.price) }}</td>
            <td :class="{ 'text-danger': p.onHand <= 0 }">{{ quantity(p.onHand) }}</td>
            <td><span class="xr-badge" :class="p.isActive ? 'xr-badge--green' : 'xr-badge--red'">{{ p.isActive ? 'فعال' : 'غیرفعال' }}</span></td>
            <td v-if="canEdit"><button type="button" class="btn btn-soft btn-sm" @click="open(p)">ویرایش / افزودن موجودی</button></td>
          </tr>
        </tbody>
      </table>
    </div>

    <UiModal v-if="editor.open" :title="editor.editing ? `ویرایش پکیج ${editor.editing.name}` : 'پکیج جدید عملیات'" @close="editor.open = false">
      <form @submit.prevent="save">
        <div class="mb-3">
          <label class="form-label" for="pk-name">نام پکیج</label>
          <input id="pk-name" v-model="editor.name" class="form-control" maxlength="150" required placeholder="مثلاً: عمل آب مروارید" />
        </div>
        <div class="row g-3 mb-3">
          <div class="col-md-6">
            <label class="form-label" for="pk-cost">قیمت خرید</label>
            <input id="pk-cost" v-model.number="editor.costPrice" class="form-control" type="number" min="0" step="0.01" placeholder="اختیاری" />
          </div>
          <div class="col-md-6">
            <label class="form-label" for="pk-sale">قیمت فروش</label>
            <input id="pk-sale" v-model.number="editor.salePrice" class="form-control" type="number" min="1" step="0.01" required />
          </div>
        </div>
        <div class="row g-3 mb-3">
          <div class="col-md-6">
            <label class="form-label" for="pk-qty">{{ editor.editing ? 'افزودن به موجودی' : 'موجودی' }}</label>
            <input id="pk-qty" v-model.number="editor.quantity" class="form-control" type="number" min="0" step="1" :required="!editor.editing" :placeholder="editor.editing ? `فعلاً ${quantity(editor.editing.onHand)}؛ خالی = بدون افزودن` : 'تعداد پکیج'" />
          </div>
          <div class="col-md-6">
            <label class="form-label" for="pk-location">گدام</label>
            <select id="pk-location" v-model="editor.location" class="form-select" required>
              <option v-for="l in locations" :key="l.code" :value="l.code">{{ l.name }}</option>
            </select>
          </div>
        </div>

        <div class="form-check mb-3">
          <input id="pk-active" v-model="editor.isActive" class="form-check-input" type="checkbox" />
          <label class="form-check-label" for="pk-active">فعال (داکتر می‌تواند انتخاب کند)</label>
        </div>

        <div v-if="editor.error" class="alert alert-danger" role="alert">{{ editor.error }}</div>
        <div class="xr-form-actions">
          <button type="submit" class="btn btn-primary" :disabled="editor.saving">{{ editor.saving ? 'در حال ذخیره…' : 'ذخیره پکیج' }}</button>
          <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
        </div>
      </form>
    </UiModal>
  </div>
</template>
