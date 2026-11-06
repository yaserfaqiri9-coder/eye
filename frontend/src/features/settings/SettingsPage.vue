<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { api, errorMessage, uploadFile } from '@/api/http'
import UiCard from '@/components/UiCard.vue'
import UiModal from '@/components/UiModal.vue'
import CallSettingsTab from '@/features/calls/CallSettingsTab.vue'
import type { ServiceRow } from '@/features/finance/types'
import { useAuthStore } from '@/stores/auth'
import { money } from '@/utils/money'
import { removeRecord } from '@/utils/removeRecord'

interface TestRow {
  id: number
  name: string
  isActive: boolean
  kind: string
  serviceId: number | null
  isSystem: boolean
}

interface RegisterRow {
  id: number
  name: string
  isActive: boolean
}

const auth = useAuthStore()
const tab = ref('hospital')
const error = ref('')
const notice = ref('')
const services = ref<ServiceRow[]>([])
const tests = ref<TestRow[]>([])
const registers = ref<RegisterRow[]>([])
const logoId = ref<number | null>(null)

const tabs = [
  { key: 'hospital', label: 'مشخصات شفاخانه' },
  { key: 'services', label: 'خدمات و تعرفه‌ها' },
  { key: 'tests', label: 'فهرست آزمایش‌ها' },
  { key: 'registers', label: 'صندوق‌ها' },
  { key: 'calls', label: 'فراخوان' },
]

const canEdit = () => auth.can('settings.edit')
const canPrice = () => auth.can('services.edit')

// These fees open the visit and the paid departments, and refuse to charge until they have a price.
const paidFlowCodes = ['VISIT-FEE', 'REFRACTION', 'IMAGING']
const unpriced = computed(() => services.value.filter((s) => paidFlowCodes.includes(s.code) && !s.currentPrice).map((s) => s.name))
const priceMinimum = computed(() => (price.row && paidFlowCodes.includes(price.row.code) ? 0.01 : 0))
const kindLabels: Record<string, string> = { Lab: 'آزمایش', Refraction: 'رفرکشن', Imaging: 'عکس‌برداری' }

const hospital = reactive({ hospitalName: '', address: '', phone: '', printFooter: '', saving: false })

function say(message: string) {
  notice.value = message
  error.value = ''
}

function fail(cause: unknown) {
  error.value = errorMessage(cause)
  notice.value = ''
}

async function load() {
  try {
    const settings = await api<typeof hospital & { logoAttachmentId: number | null }>('/settings')
    Object.assign(hospital, { hospitalName: settings.hospitalName ?? '', address: settings.address ?? '', phone: settings.phone ?? '', printFooter: settings.printFooter ?? '' })
    logoId.value = settings.logoAttachmentId
    services.value = await api<ServiceRow[]>('/services')
    if (auth.can('settings.view')) {
      tests.value = await api<TestRow[]>('/diagnostic-tests/manage')
      registers.value = await api<RegisterRow[]>('/cash/registers/all')
    }
  } catch (cause) {
    fail(cause)
  }
}

async function saveHospital() {
  hospital.saving = true
  try {
    await api('/settings', { method: 'PUT', body: { hospitalName: hospital.hospitalName, address: hospital.address, phone: hospital.phone, printFooter: hospital.printFooter } })
    say('ذخیره شد.')
  } catch (cause) {
    fail(cause)
  } finally {
    hospital.saving = false
  }
}

async function uploadLogo(event: Event) {
  const file = (event.target as HTMLInputElement).files?.[0]
  if (!file) return
  const form = new FormData()
  form.set('ownerType', 'Logo')
  form.set('ownerId', '0')
  form.set('file', file)
  try {
    logoId.value = (await uploadFile<{ id: number }>('/attachments', form)).id
    say('لوگو ذخیره شد.')
  } catch (cause) {
    fail(cause)
  }
}

async function removeLogo() {
  if (!logoId.value || !window.confirm('لوگو حذف شود؟')) return
  try {
    await api(`/attachments/${logoId.value}`, { method: 'DELETE' })
    logoId.value = null
    say('لوگو حذف شد.')
  } catch (cause) {
    fail(cause)
  }
}

const service = reactive({ open: false, id: null as number | null, code: '', name: '', price: 0, isActive: true, saving: false, error: '' })

function openService(row?: ServiceRow) {
  Object.assign(service, { open: true, id: row?.id ?? null, code: row?.code ?? '', name: row?.name ?? '', price: 0, isActive: row?.isActive ?? true, saving: false, error: '' })
}

/** The delete action shared by the lists on this page. */
async function removeFrom(path: string, label: string, reload: () => Promise<void>) {
  error.value = ''
  notice.value = ''
  try {
    notice.value = await removeRecord(path, label)
    if (notice.value) await reload()
  } catch (cause) {
    fail(cause)
  }
}

const reloadServices = async () => { services.value = await api<ServiceRow[]>('/services') }
const reloadTests = async () => { tests.value = await api<TestRow[]>('/diagnostic-tests/manage') }
const reloadRegisters = async () => { registers.value = await api<RegisterRow[]>('/cash/registers/all') }

async function saveService() {
  service.saving = true
  service.error = ''
  try {
    if (service.id) await api(`/services/${service.id}`, { method: 'PUT', body: { code: service.code, name: service.name, isActive: service.isActive } })
    else await api('/services', { method: 'POST', body: { code: service.code, name: service.name, isActive: true, price: service.price } })
    service.open = false
    services.value = await api<ServiceRow[]>('/services')
  } catch (cause) {
    service.error = errorMessage(cause)
  } finally {
    service.saving = false
  }
}

const price = reactive({ row: null as ServiceRow | null, value: 0, saving: false, error: '' })

async function savePrice() {
  price.saving = true
  price.error = ''
  try {
    await api(`/services/${price.row!.id}/price`, { method: 'POST', body: { price: price.value } })
    price.row = null
    services.value = await api<ServiceRow[]>('/services')
  } catch (cause) {
    price.error = errorMessage(cause)
  } finally {
    price.saving = false
  }
}

const test = reactive({ open: false, id: null as number | null, name: '', kind: 'Lab', serviceId: null as number | null, isActive: true, saving: false, error: '' })

function openTest(row?: TestRow) {
  Object.assign(test, { open: true, id: row?.id ?? null, name: row?.name ?? '', kind: row?.kind ?? 'Lab', serviceId: row?.serviceId ?? null, isActive: row?.isActive ?? true, saving: false, error: '' })
}

async function saveTest() {
  test.saving = true
  test.error = ''
  const body = { name: test.name, isActive: test.isActive, serviceId: test.serviceId, kind: test.kind }
  try {
    if (test.id) await api(`/diagnostic-tests/${test.id}`, { method: 'PUT', body })
    else await api('/diagnostic-tests', { method: 'POST', body })
    test.open = false
    await reloadTests()
  } catch (cause) {
    test.error = errorMessage(cause)
  } finally {
    test.saving = false
  }
}

const registerName = ref('')

async function saveRegister() {
  try {
    await api('/cash/registers', { method: 'POST', body: { name: registerName.value } })
    registerName.value = ''
    await reloadRegisters()
  } catch (cause) {
    fail(cause)
  }
}

const register = reactive({ id: null as number | null, name: '', isActive: true, saving: false, error: '' })

async function updateRegister() {
  register.saving = true
  register.error = ''
  try {
    await api(`/cash/registers/${register.id}`, { method: 'PUT', body: { name: register.name, isActive: register.isActive } })
    register.id = null
    await reloadRegisters()
  } catch (cause) {
    register.error = errorMessage(cause)
  } finally {
    register.saving = false
  }
}

onMounted(load)
</script>

<template>
  <div v-if="error" class="alert alert-danger" role="alert">{{ error }}</div>
  <div v-if="notice" class="alert alert-success" role="status">{{ notice }}</div>

  <section class="xr-card">
    <div class="xr-card__header flex-wrap">
      <nav class="xr-tabs" role="tablist" aria-label="بخش‌های تنظیمات">
        <button v-for="t in tabs" :key="t.key" type="button" role="tab" class="xr-tab" :class="{ 'xr-tab--active': tab === t.key }" :aria-selected="tab === t.key" @click="tab = t.key">{{ t.label }}</button>
      </nav>
    </div>
    <div class="xr-card__body">
      <form v-if="tab === 'hospital'" @submit.prevent="saveHospital">
        <fieldset :disabled="!canEdit()">
          <div class="row g-3 mb-3">
            <div class="col-md-6"><label class="form-label" for="h-name">نام شفاخانه</label><input id="h-name" v-model="hospital.hospitalName" class="form-control" maxlength="150" /></div>
            <div class="col-md-6"><label class="form-label" for="h-phone">تماس</label><input id="h-phone" v-model="hospital.phone" class="form-control" dir="ltr" maxlength="60" /></div>
            <div class="col-12"><label class="form-label" for="h-address">آدرس</label><input id="h-address" v-model="hospital.address" class="form-control" maxlength="300" /></div>
            <div class="col-12"><label class="form-label" for="h-footer">پاورقی چاپ</label><input id="h-footer" v-model="hospital.printFooter" class="form-control" maxlength="300" /></div>
            <div class="col-12">
              <label class="form-label" for="h-logo">لوگو (PNG یا JPEG)</label>
              <input id="h-logo" class="form-control" type="file" accept="image/png,image/jpeg" @change="uploadLogo" />
              <img v-if="logoId" :src="`/api/attachments/${logoId}`" alt="لوگوی فعلی" class="mt-2" style="max-height: 60px" />
              <button v-if="logoId" type="button" class="btn btn-soft-danger btn-sm mt-2 ms-2" @click="removeLogo">حذف لوگو</button>
            </div>
          </div>
          <button type="submit" class="btn btn-primary" :disabled="hospital.saving">ذخیره</button>
        </fieldset>
      </form>

      <template v-else-if="tab === 'services'">
        <button v-if="canEdit()" type="button" class="btn btn-primary mb-3" @click="openService()">خدمت جدید</button>
        <div v-if="unpriced.length" class="alert alert-warning" role="alert">
          تا قیمت این خدمات ثبت نشود، نوبت و درخواست مربوط ثبت نمی‌شود: {{ unpriced.join('، ') }}. مبلغ را مسئول مالی تعیین می‌کند.
        </div>
        <p v-if="services.length === 0" class="text-muted">خدمتی تعریف نشده است.</p>
        <div v-else class="xr-table-wrap">
          <table class="xr-table">
            <thead><tr><th>کد</th><th>نام</th><th>قیمت فعلی</th><th>وضعیت</th><th v-if="canPrice() || canEdit()">عملیات</th></tr></thead>
            <tbody>
              <tr v-for="row in services" :key="row.id">
                <td><span class="ltr-num">{{ row.code }}</span></td>
                <td>{{ row.name }}</td>
                <td>
                  <template v-if="row.currentPrice">{{ money(row.currentPrice) }}</template>
                  <span v-else-if="paidFlowCodes.includes(row.code)" class="xr-badge xr-badge--orange">تعیین نشده</span>
                  <template v-else>{{ money(row.currentPrice) }}</template>
                </td>
                <td><span class="xr-badge" :class="row.isActive ? 'xr-badge--green' : 'xr-badge--red'">{{ row.isActive ? 'فعال' : 'غیرفعال' }}</span></td>
                <td v-if="canPrice() || canEdit()" class="text-nowrap">
                  <button v-if="canPrice()" type="button" class="btn btn-soft btn-sm" @click="Object.assign(price, { row, value: row.currentPrice ?? 0, error: '' })">{{ row.currentPrice ? 'تغییر قیمت' : 'ثبت قیمت' }}</button>
                  <button v-if="canEdit()" type="button" class="btn btn-soft btn-sm ms-2" @click="openService(row)">ویرایش</button>
                  <button v-if="canEdit() && !paidFlowCodes.includes(row.code)" type="button" class="btn btn-soft-danger btn-sm ms-2" @click="removeFrom(`/services/${row.id}`, row.name, reloadServices)">حذف</button>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      </template>

      <template v-else-if="tab === 'tests'">
        <button v-if="canEdit()" type="button" class="btn btn-primary mb-3" @click="openTest()">آزمایش جدید</button>
        <p v-if="tests.length === 0" class="text-muted">آزمایشی تعریف نشده یا اجازه مشاهده ندارید.</p>
        <div v-else class="xr-table-wrap">
          <table class="xr-table">
            <thead><tr><th>نام</th><th>نوع</th><th>وضعیت</th><th v-if="canEdit()">عملیات</th></tr></thead>
            <tbody>
              <tr v-for="row in tests" :key="row.id">
                <td>{{ row.name }}</td>
                <td>{{ kindLabels[row.kind] ?? row.kind }}</td>
                <td><span class="xr-badge" :class="row.isActive ? 'xr-badge--green' : 'xr-badge--red'">{{ row.isActive ? 'فعال' : 'غیرفعال' }}</span></td>
                <td v-if="canEdit()" class="text-nowrap">
                  <span v-if="row.isSystem" class="text-muted small">سیستمی</span>
                  <template v-else>
                    <button type="button" class="btn btn-soft btn-sm" @click="openTest(row)">ویرایش</button>
                    <button type="button" class="btn btn-soft-danger btn-sm ms-2" @click="removeFrom(`/diagnostic-tests/${row.id}`, row.name, reloadTests)">حذف</button>
                  </template>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      </template>

      <CallSettingsTab v-else-if="tab === 'calls'" />

      <template v-else>
        <form v-if="canEdit()" class="row g-2 mb-3" @submit.prevent="saveRegister">
          <div class="col-md-5"><input v-model="registerName" class="form-control" placeholder="نام صندوق جدید" aria-label="نام صندوق جدید" maxlength="100" required /></div>
          <div class="col-auto"><button type="submit" class="btn btn-primary">افزودن صندوق</button></div>
        </form>
        <p v-if="registers.length === 0" class="text-muted">صندوقی نمایش داده نمی‌شود یا اجازه مشاهده ندارید.</p>
        <div v-else class="xr-table-wrap">
          <table class="xr-table">
            <thead><tr><th>نام</th><th>وضعیت</th><th v-if="canEdit()">عملیات</th></tr></thead>
            <tbody>
              <tr v-for="row in registers" :key="row.id">
                <td>{{ row.name }}</td>
                <td><span class="xr-badge" :class="row.isActive ? 'xr-badge--green' : 'xr-badge--red'">{{ row.isActive ? 'فعال' : 'غیرفعال' }}</span></td>
                <td v-if="canEdit()" class="text-nowrap">
                  <button type="button" class="btn btn-soft btn-sm" @click="Object.assign(register, { id: row.id, name: row.name, isActive: row.isActive, saving: false, error: '' })">ویرایش</button>
                  <button type="button" class="btn btn-soft-danger btn-sm ms-2" @click="removeFrom(`/cash/registers/${row.id}`, row.name, reloadRegisters)">حذف</button>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      </template>
    </div>
  </section>

  <UiModal v-if="register.id" title="ویرایش صندوق" @close="register.id = null">
    <form @submit.prevent="updateRegister">
      <div class="mb-3"><label class="form-label" for="rg-name">نام صندوق</label><input id="rg-name" v-model="register.name" class="form-control" maxlength="100" required /></div>
      <label class="form-check-label mb-3"><input v-model="register.isActive" type="checkbox" class="form-check-input ms-2" />فعال</label>
      <div v-if="register.error" class="alert alert-danger" role="alert">{{ register.error }}</div>
      <div class="xr-form-actions">
        <button type="submit" class="btn btn-primary" :disabled="register.saving">ذخیره</button>
        <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
      </div>
    </form>
  </UiModal>

  <UiModal v-if="service.open" :title="service.id ? 'ویرایش خدمت' : 'خدمت جدید'" @close="service.open = false">
    <form @submit.prevent="saveService">
      <div class="mb-3"><label class="form-label" for="sv-code">کد</label><input id="sv-code" v-model="service.code" class="form-control" dir="ltr" maxlength="30" required :disabled="!!service.id && paidFlowCodes.includes(service.code)" /></div>
      <div class="mb-3"><label class="form-label" for="sv-name">نام خدمت</label><input id="sv-name" v-model="service.name" class="form-control" maxlength="150" required /></div>
      <div v-if="!service.id" class="mb-3"><label class="form-label" for="sv-price">قیمت</label><input id="sv-price" v-model.number="service.price" class="form-control" type="number" min="0" step="0.01" required /></div>
      <label v-else-if="!paidFlowCodes.includes(service.code)" class="form-check-label mb-3"><input v-model="service.isActive" type="checkbox" class="form-check-input ms-2" />فعال</label>
      <div v-if="service.error" class="alert alert-danger" role="alert">{{ service.error }}</div>
      <div class="xr-form-actions">
        <button type="submit" class="btn btn-primary" :disabled="service.saving">ذخیره</button>
        <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
      </div>
    </form>
  </UiModal>

  <UiModal v-if="price.row" :title="`قیمت ${price.row.name}`" @close="price.row = null">
    <form @submit.prevent="savePrice">
      <p class="small text-muted">قیمت جدید فقط روی فاکتورهای بعدی اثر دارد.</p>
      <label class="form-label" for="pr-value">قیمت جدید</label>
      <input id="pr-value" v-model.number="price.value" class="form-control mb-3" type="number" :min="priceMinimum" step="0.01" required />
      <div v-if="price.error" class="alert alert-danger" role="alert">{{ price.error }}</div>
      <div class="xr-form-actions">
        <button type="submit" class="btn btn-primary" :disabled="price.saving">ذخیره</button>
        <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
      </div>
    </form>
  </UiModal>

  <UiModal v-if="test.open" :title="test.id ? 'ویرایش آزمایش' : 'آزمایش جدید'" @close="test.open = false">
    <form @submit.prevent="saveTest">
      <div class="mb-3"><label class="form-label" for="ts-name">نام آزمایش</label><input id="ts-name" v-model="test.name" class="form-control" maxlength="150" required /></div>
      <div class="mb-3">
        <label class="form-label" for="ts-kind">نوع</label>
        <select id="ts-kind" v-model="test.kind" class="form-select" :disabled="!!test.id">
          <option value="Lab">آزمایش (پرداخت هر زمان)</option>
          <option value="Imaging">عکس‌برداری (پرداخت پیش از شروع)</option>
        </select>
      </div>
      <div class="mb-3">
        <label class="form-label" for="ts-service">خدمت قابل پرداخت</label>
        <select id="ts-service" v-model.number="test.serviceId" class="form-select">
          <option :value="null">بدون هزینه خودکار</option>
          <option v-for="s in services.filter((x) => x.isActive || x.id === test.serviceId)" :key="s.id" :value="s.id">{{ s.name }}</option>
        </select>
      </div>
      <label v-if="test.id" class="form-check-label mb-3"><input v-model="test.isActive" type="checkbox" class="form-check-input ms-2" />فعال</label>
      <div v-if="test.error" class="alert alert-danger" role="alert">{{ test.error }}</div>
      <div class="xr-form-actions">
        <button type="submit" class="btn btn-primary" :disabled="test.saving">ذخیره</button>
        <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
      </div>
    </form>
  </UiModal>
</template>
